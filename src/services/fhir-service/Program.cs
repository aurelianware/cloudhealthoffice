using CloudHealthOffice.PlanConfiguration;
using FhirService.Formatters;
using CloudHealthOffice.Infrastructure.Extensions;
using FhirService.Middleware;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Identity;
using FhirService.Services.ChoNative;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.ProviderEnrollmentService.Configuration;
using CloudHealthOffice.PriorAuthRuleEngine.Configuration;
using MongoDB.Driver;
using CloudHealthOffice.ManagedFhirStore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── SMART on FHIR / OAuth trust ───────────────────────────────────────────────
// Multi-issuer, fail-closed trust: the issuer is resolved first and supplies
// its own keys, audiences, algorithms and claim mapping. Demo mode trusts the
// bundled smart-auth-service and is refused outside a development host, so a
// production deployment cannot silently fall back to it.
// See docs/architecture/smart-oauth-trust.md.
builder.Services.AddSmartTrust(builder.Configuration, builder.Environment);

// ── CHO authentication (internal callers) ─────────────────────────────────────
// fhir-service serves two kinds of caller, each under its own scheme:
//   * SMART ("Smart"): external FHIR clients. Scopes + patient binding decide.
//   * CHO ("Bearer", AddChoAuthentication): the portal and CHO services. The
//     tenant comes from the token and CHO permissions decide.
// The FhirCaller selector routes each token to exactly one of them by issuer;
// every endpoint states which callers it serves with [FhirAccess]. No default
// permissions: an unannotated action is denied.
//
// Outbound: SmartCallerOutboundHandler is registered BEFORE AddChoAuthentication
// so it is the outer handler on every factory client. For a SMART caller it
// sends fhir-service's own service token for the caller's tenant to CHO
// services and never the SMART token; CHO callers' tokens are forwarded by the
// shared ChoOutboundTokenHandler.
builder.Services.AddTransient<SmartCallerOutboundHandler>();
builder.Services.ConfigureHttpClientDefaults(client =>
    client.AddHttpMessageHandler<SmartCallerOutboundHandler>());

// Conformance discovery is read before a client has a tenant binding, so these
// paths skip tenant resolution like the probes do. They serve no tenant data.
builder.Services.AddSingleton(new CloudHealthOffice.Infrastructure.Middleware.TenantMiddlewareOptions
{
    PassthroughPaths =
    [
        "/health", "/ready", "/live", "/metrics",
        "/fhir/r4/metadata", "/fhir/r4/.well-known", "/fhir/r4/adapter-status",
        "/fhir/r4/StructureDefinition", "/fhir/r4/OperationDefinition",
        "/fhir/r4/CodeSystem", "/fhir/r4/ValueSet",
        // The public Provider Directory takes its tenant from the published
        // directory id, never from a token (PublicProviderDirectoryController).
        "/fhir/r4/directory",
    ],
});
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddFhirCallerSchemes(builder.Configuration);
builder.Services.AddFhirAccessPolicies();

// ── Public Provider Directory (CMS-9115-F, D-29) ─────────────────────────────
// Directories are published per plan in configuration; none by default.
builder.Services.Configure<FhirService.Services.PublicDirectory.PublicProviderDirectoryOptions>(
    builder.Configuration.GetSection(FhirService.Services.PublicDirectory.PublicProviderDirectoryOptions.SectionName));
var publicDirectoryPermit = builder.Configuration.GetValue(
    $"{FhirService.Services.PublicDirectory.PublicProviderDirectoryOptions.SectionName}:PermitPerMinute", 300);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Unauthenticated, so the only key is the client address. Behind a proxy
    // that does not forward it, every caller shares one bucket: size the
    // permit for that.
    options.AddPolicy(FhirService.Controllers.PublicProviderDirectoryController.RateLimitPolicy, http =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = publicDirectoryPermit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ── Shared infrastructure ─────────────────────────────────────────────────────
// ICacheProvider (via AddChoCaching) backs the tenant-config and rule-set
// K/V caches. On the Redis backend path it also registers a shared
// IConnectionMultiplexer in DI so RedisPaRuleRepository's SCAN-based
// state-flush — a deliberate exception to ICacheProvider — can inject
// it. See docs/architecture/shared-cache.md.
builder.Services.AddChoCaching(builder.Configuration, builder.Environment);

// Database: MongoDB (ADR 017). Required in every environment, Development and
// Testing included: there is no in-process fallback, so a host without
// MongoDb:ConnectionString refuses to start.
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);
if (databaseProvider is null)
{
    throw new InvalidOperationException(
        "fhir-service requires a database. Configure MongoDb:ConnectionString.");
}
if (databaseProvider != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "fhir-service supports MongoDB only. Remove Database:Provider or set it to MongoDb.");
}

// ── Provider Enrollment Service ───────────────────────────────────────────────
// Supplies IEnrollmentDecisionGate → PasAutoAdjudicator Rule 0.
// TenantEnrollmentConfig cached in Redis (5 min TTL, invalidated on write).
//
// Required appsettings.json:
//   "ProviderEnrollmentService": {
//     "TenantConfigCacheTtlSeconds": 300,
//     "Tmhp": { "ApiKey": "...(from AKV)" },
//     "Caqh": { "Username": "...", "Password": "...(from AKV)" }
//   }
// Cache backend presence is decided by AddChoCaching (Redis when a
// connection string is configured AND env is Production; InMemory
// otherwise). Either resolves to a working ICacheProvider.
builder.Services.AddProviderEnrollmentService(builder.Configuration)
    .UseMongoRepositories().WithTenantConfigCache()
    .WithTexasSource().WithCaqhSource();

// ── Prior Auth Rule Engine ────────────────────────────────────────────────────
// Supplies IPriorAuthRuleEngine → PasAutoAdjudicator Rule 5.
// Rule sets cached via ICacheProvider (15 min TTL, invalidated on admin write).
// Seeds TX platform rules (STAR / STARPlus / STARKids) on first deployment.
//
// Required appsettings.json:
//   "PriorAuthRuleEngine": {
//     "RuleSetCacheTtl": "00:15:00",
//     "GoldCardLookbackDays": 180,
//     "PendOnRuleError": true
//   }
builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
    .UseMongoRepository().WithRuleCache()
    .WithPlatformRules().SeedOnStartup();

// ── FHIR data adapters ────────────────────────────────────────────────────────
builder.Services.Configure<FhirAdapterOptions>(
    builder.Configuration.GetSection(FhirAdapterOptions.SectionName));
builder.Services.AddSingleton<IFhirAdapterStatusService, FhirAdapterStatusService>();
// Patient, Coverage, Encounter and Claim may be served from CHO's own
// member-service, coverage-service, encounter-service and claims-service
// (FhirAdapters:Sources:<Resource>=ChoNative) instead of the synthetic mock,
// which stays the default (ADR 018, ADR 024). The bindings are resolved
// and validated here, so an invalid or unsatisfiable configuration stops
// start-up instead of serving something other than what it says.
var fhirDataBindings = FhirDataBindings.From(
    builder.Configuration.GetSection(FhirAdapterOptions.SectionName).Get<FhirAdapterOptions>()
    ?? new FhirAdapterOptions());
builder.Services.AddSingleton(fhirDataBindings);
builder.Services.AddFhirSyntheticSources(fhirDataBindings, builder.Configuration, builder.Environment);
if (fhirDataBindings.AnyNative)
{
    void AddChoNativeClient(string name, string urlKey, string resource)
    {
        var url = builder.Configuration[urlKey];
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                $"FhirAdapters:Sources:{resource}=ChoNative needs {urlKey} set to the service's absolute URL.");
        }
        builder.Services.AddHttpClient(name, client =>
            {
                client.BaseAddress = baseUri;
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .AddHttpMessageHandler<TenantHeaderPropagationHandler>()
            .AddHttpMessageHandler<CorrelationIdPropagationHandler>();
    }

    // Payer-to-Payer needs both (ADR 020), whatever the Patient and Coverage bindings.
    if (fhirDataBindings.Patient == FhirAdapterSources.ChoNative || fhirDataBindings.PayerToPayerNative)
        AddChoNativeClient(ChoNativeFhirDataAdapter.MemberServiceClient, "Services:MemberServiceUrl",
            fhirDataBindings.Patient == FhirAdapterSources.ChoNative ? "Patient" : "PayerToPayer");
    if (fhirDataBindings.Coverage == FhirAdapterSources.ChoNative || fhirDataBindings.PayerToPayerNative)
        AddChoNativeClient(ChoNativeFhirDataAdapter.CoverageServiceClient, "Services:CoverageServiceUrl",
            fhirDataBindings.Coverage == FhirAdapterSources.ChoNative ? "Coverage" : "PayerToPayer");
    if (fhirDataBindings.Encounter == FhirAdapterSources.ChoNative)
        AddChoNativeClient(ChoNativeFhirDataAdapter.EncounterServiceClient, "Services:EncounterServiceUrl", "Encounter");
    if (fhirDataBindings.Claim == FhirAdapterSources.ChoNative)
        AddChoNativeClient(ChoNativeFhirDataAdapter.ClaimsServiceClient, "Services:ClaimsServiceUrl", "Claim");
    builder.Services.AddSingleton<ChoNativeFhirDataAdapter>();
    builder.Services.AddScoped<ChoNativeMemberDirectory>();
}
builder.Services.AddSingleton<IFhirDataAdapter>(sp => new RoutingFhirDataAdapter(
    sp.GetRequiredService<FhirDataBindings>(),
    sp.GetRequiredService<MockFhirDataAdapter>(),
    sp.GetService<ChoNativeFhirDataAdapter>()));
builder.Services.AddSingleton<FhirBundleBuilder>();
builder.Services.AddSingleton<ICms0057ComplianceChecker, Cms0057ComplianceChecker>();

// ── Payer-to-Payer inbound respond (CMS-0057-F P2P-01) ────────────────────────
// CHO-native member-data export over CHO-owned data (reuses the Patient Access
// data provider + mapper). Tenant-scoped, deterministic member resolution with a
// consent/authorization gate. Outbound initiation (P2P-02) is wired separately
// below.
// The member, coverage and claims behind Payer-to-Payer: the mock directory, or
// CHO's own services when FhirAdapters:Sources:PayerToPayer is ChoNative (ADR 020).
if (fhirDataBindings.PayerToPayerNative)
{
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerMemberSource,
        FhirService.Services.PayerToPayer.ChoNative.ChoNativePayerToPayerMemberSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerExportSource,
        FhirService.Services.PayerToPayer.ChoNative.ChoNativePayerToPayerExportSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerMemberMatchSource,
        FhirService.Services.PayerToPayer.ChoNative.ChoNativePayerToPayerMemberMatchSource>();
}
else
{
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerMemberSource,
        FhirService.Services.PayerToPayer.PatientAccessPayerToPayerMemberSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerExportSource,
        FhirService.Services.PayerToPayer.PatientAccessPayerToPayerExportSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerMemberMatchSource,
        FhirService.Services.PayerToPayer.PatientAccessPayerToPayerMemberMatchSource>();
}
builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerMemberResolver,
    FhirService.Services.PayerToPayer.PayerToPayerMemberResolver>();
// Payer-to-Payer authorization. The gate applies the shared
// ConsentAuthorizationPolicy for the PayerToPayerExchange purpose, so inbound
// respond and outbound initiation reach the same answer from the same rules; a
// consent granted for another purpose (or none) never opens the exchange.
builder.Services.Configure<FhirService.Services.PayerToPayer.PayerToPayerConsentOptions>(
    builder.Configuration.GetSection(
        FhirService.Services.PayerToPayer.PayerToPayerConsentOptions.SectionName));

// The registry in consent-service is authoritative when its base URL is
// configured; otherwise the configured records serve Development and demos with
// the same purpose/status/period semantics. Neither is a boolean allow-list.
var consentServiceUrl = builder.Configuration["Services:ConsentServiceUrl"];
if (!string.IsNullOrWhiteSpace(consentServiceUrl))
{
    builder.Services.AddHttpClient(
            FhirService.Services.PayerToPayer.HttpConsentRegistryConsentSource.HttpClientName,
            client => client.BaseAddress = new Uri(consentServiceUrl))
        .AddHttpMessageHandler<TenantHeaderPropagationHandler>()
        .AddHttpMessageHandler<CorrelationIdPropagationHandler>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.HttpConsentRegistryConsentSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerConsentSource>(sp =>
        sp.GetRequiredService<FhirService.Services.PayerToPayer.HttpConsentRegistryConsentSource>());
    builder.Services.AddScoped<FhirService.Services.Consent.IConsentSource>(sp =>
        sp.GetRequiredService<FhirService.Services.PayerToPayer.HttpConsentRegistryConsentSource>());
}
else if (FhirService.Services.Consent.ConsentSourceSelection.UseConfiguredCatalog(
             builder.Environment, builder.Configuration))
{
    // Development, or a deployment that deliberately configured a consent
    // catalog (a demo): the configured records stand in for the registry.
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.ConfiguredPayerToPayerConsentSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerConsentSource>(sp =>
        sp.GetRequiredService<FhirService.Services.PayerToPayer.ConfiguredPayerToPayerConsentSource>());
    builder.Services.AddScoped<FhirService.Services.Consent.IConsentSource>(sp =>
        sp.GetRequiredService<FhirService.Services.PayerToPayer.ConfiguredPayerToPayerConsentSource>());
}
else
{
    // No registry and no catalog outside Development: nothing can show what a
    // member chose. Every consent decision refuses (Provider Access shares only
    // when the registry shows no opt-out, ADR 021) rather than reading an empty
    // catalog as "nobody opted out".
    builder.Services.AddScoped<FhirService.Services.Consent.UnavailableConsentSource>();
    builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerConsentSource>(sp =>
        sp.GetRequiredService<FhirService.Services.Consent.UnavailableConsentSource>());
    builder.Services.AddScoped<FhirService.Services.Consent.IConsentSource>(sp =>
        sp.GetRequiredService<FhirService.Services.Consent.UnavailableConsentSource>());
}

// ONE evaluator for every purpose: the same fail-closed registry read and the
// same ConsentAuthorizationPolicy answer Payer-to-Payer and Provider Access, so
// neither can drift more permissive than the other.
builder.Services.AddScoped<FhirService.Services.Consent.IConsentEvaluator,
    FhirService.Services.Consent.RegistryConsentEvaluator>();

builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerConsentGate,
    FhirService.Services.PayerToPayer.ConsentRegistryPayerToPayerConsentGate>();

// Provider Access authorization (CONSENT-01). Attribution and the
// ProviderAccess opt-out decision (D-11), composed on top of the
// authentication and SMART-scope controls the middleware already enforces.
builder.Services.Configure<FhirService.Services.ProviderAccess.ProviderAttributionOptions>(
    builder.Configuration.GetSection(
        FhirService.Services.ProviderAccess.ProviderAttributionOptions.SectionName));
builder.Services.AddScoped<FhirService.Services.ProviderAccess.ConfiguredProviderAttributionSource>();

// Attribution sources. Configured panels are always available; claims history
// (PROV-02) is added when a deployment turns it on, and the two compose as a
// disjunction — an assignment the panel knows about and treatment the claims
// know about are different relationships, and either one is a relationship.
// Which sources are active is reported by /fhir/r4/adapter-status, so a render
// cannot quietly be running on demo panels.
var claimsHistoryAttribution = builder.Configuration.GetValue(
    "Cms0057:ProviderAttribution:ClaimsHistory:Enabled", false);
if (claimsHistoryAttribution)
{
    var lookbackMonths = builder.Configuration.GetValue(
        "Cms0057:ProviderAttribution:ClaimsHistory:LookbackMonths", 24);
    if (lookbackMonths <= 0)
    {
        // Refused at start-up rather than at the first request: a non-positive
        // lookback attributes no one, which is indistinguishable from a plan
        // whose providers genuinely have not treated anybody.
        throw new InvalidOperationException(
            "Cms0057:ProviderAttribution:ClaimsHistory:LookbackMonths must be greater than zero "
            + "when claims-history attribution is enabled.");
    }

    builder.Services.AddScoped<FhirService.Services.ProviderAccess.ClaimsHistoryProviderAttributionSource>();
    builder.Services.AddScoped<FhirService.Services.ProviderAccess.IProviderAttributionSource>(sp =>
        new FhirService.Services.ProviderAccess.CompositeProviderAttributionSource(
            new FhirService.Services.ProviderAccess.IProviderAttributionSource[]
            {
                sp.GetRequiredService<FhirService.Services.ProviderAccess.ConfiguredProviderAttributionSource>(),
                sp.GetRequiredService<FhirService.Services.ProviderAccess.ClaimsHistoryProviderAttributionSource>(),
            }));
}
else
{
    builder.Services.AddScoped<FhirService.Services.ProviderAccess.IProviderAttributionSource>(sp =>
        sp.GetRequiredService<FhirService.Services.ProviderAccess.ConfiguredProviderAttributionSource>());
}
builder.Services.AddScoped<FhirService.Services.ProviderAccess.IProviderAccessAuthorizationService,
    FhirService.Services.ProviderAccess.ProviderAccessAuthorizationService>();
builder.Services.AddSingleton<FhirService.Services.PayerToPayer.IPayerToPayerExportBuilder,
    FhirService.Services.PayerToPayer.PayerToPayerExportBuilder>();
builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerExchangeService,
    FhirService.Services.PayerToPayer.PayerToPayerExchangeService>();

// ── Payer-to-Payer member match (CMS-0057-F P2P-04) ───────────────────────────
// Deterministic, fail-safe cross-payer identity resolution + coverage selection
// over the same CHO-owned member/coverage directory. Identity only; the P2P-01
// respond path enforces consent when data is actually returned.
builder.Services.AddScoped<FhirService.Services.PayerToPayer.IPayerToPayerMemberMatchService,
    FhirService.Services.PayerToPayer.PayerToPayerMemberMatchService>();

// ── Payer-to-Payer outbound initiation (CMS-0057-F P2P-02) ────────────────────
// CHO, as the member's new/current payer, initiates the exchange against a
// prior payer: trusted-directory endpoint resolution, server-side opt-in gate,
// remote $member-match, then member-data export, response validation, and a
// durable exchange record. The remote side sits behind IPayerToPayerRemoteClient
// so the workflow carries no HTTP detail, and target payers come only from
// configuration — a caller names a payer id, never a URL (SSRF).
builder.Services.Configure<FhirService.Services.PayerToPayer.Outbound.PayerToPayerDirectoryOptions>(
    builder.Configuration.GetSection(
        FhirService.Services.PayerToPayer.Outbound.PayerToPayerDirectoryOptions.SectionName));
builder.Services.Configure<FhirService.Services.PayerToPayer.Outbound.PayerToPayerTransportOptions>(
    builder.Configuration.GetSection(
        FhirService.Services.PayerToPayer.Outbound.PayerToPayerTransportOptions.SectionName));
builder.Services.AddSingleton<FhirService.Services.PayerToPayer.Outbound.IPayerToPayerEndpointResolver,
    FhirService.Services.PayerToPayer.Outbound.ConfiguredPayerToPayerEndpointResolver>();
// Payer-to-Payer transport credentials (SMART Backend Services / UDAP client
// registration, mTLS) are negotiated per trading partner and are deployment
// integration work; the default provider supplies none rather than fabricating
// one, so an unonboarded peer answers Unauthorized.
builder.Services.AddSingleton<FhirService.Services.PayerToPayer.Outbound.IPayerToPayerCredentialProvider,
    FhirService.Services.PayerToPayer.Outbound.UnconfiguredPayerToPayerCredentialProvider>();
builder.Services.AddSingleton<
    FhirService.Services.PayerToPayer.Outbound.IPayerToPayerOutboundExchangeStore,
    FhirService.Services.PayerToPayer.Outbound.InMemoryPayerToPayerOutboundExchangeStore>();
builder.Services.AddScoped<FhirService.Services.PayerToPayer.Outbound.IPayerToPayerRemoteClient,
    FhirService.Services.PayerToPayer.Outbound.HttpPayerToPayerRemoteClient>();
builder.Services.AddScoped<FhirService.Services.PayerToPayer.Outbound.IPayerToPayerOutboundService,
    FhirService.Services.PayerToPayer.Outbound.PayerToPayerOutboundService>();
// Durable ingestion of a validated Payer-to-Payer package into CHO's imported
// member record. The import store is deliberately separate from CHO's
// authoritative member/enrollment/claim stores, so a prior payer's data can
// never become CHO-owned.
//
// Singleton: the repository ensures its indexes at construction, so a
// per-request instance would re-issue createIndex on every call. It holds
// only collection handles and is safe to share.
// Payer-to-Payer consents accepted from requesting payers, one per (tenant,
// member, payer): an export is served only to the payer whose consent is on
// record (D-13, ADR 022). Singleton for the same index reason as below.
builder.Services.AddSingleton<
    FhirService.Services.PayerToPayer.IPayerToPayerConsentAttestationStore,
    FhirService.Services.PayerToPayer.MongoPayerToPayerConsentAttestationStore>();
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(
    builder.Services, TimeProvider.System);

builder.Services.AddSingleton<
    FhirService.Services.PayerToPayer.Ingestion.IPayerToPayerImportRepository,
    FhirService.Services.PayerToPayer.Ingestion.MongoPayerToPayerImportRepository>();
builder.Services.AddScoped<
    FhirService.Services.PayerToPayer.Ingestion.IPayerToPayerPackageIngestionService,
    FhirService.Services.PayerToPayer.Ingestion.PayerToPayerPackageIngestionService>();

// ── USCDI clinical resources (CMS-0057-F PAT-02) ──────────────────────────────
// The clinical serving store is the SAME object as the Payer-to-Payer import
// store, resolved through a second interface — the rows Patient and Provider
// Access read are the rows an exchange committed. Registering it as a forwarding
// singleton rather than a second registration is what makes that literal: there
// is exactly one instance, so there is no second copy of a clinical resource and
// no projection to fall behind.
builder.Services.AddSingleton<FhirService.Services.Clinical.IClinicalResourceStore>(sp =>
    (FhirService.Services.Clinical.IClinicalResourceStore)
        sp.GetRequiredService<FhirService.Services.PayerToPayer.Ingestion.IPayerToPayerImportRepository>());
builder.Services.AddSingleton<FhirService.Services.Clinical.IClinicalBackfillStore>(sp =>
    (FhirService.Services.Clinical.IClinicalBackfillStore)
        sp.GetRequiredService<FhirService.Services.PayerToPayer.Ingestion.IPayerToPayerImportRepository>());

builder.Services.Configure<FhirService.Services.Clinical.ClinicalBackfillOptions>(
    builder.Configuration.GetSection(FhirService.Services.Clinical.ClinicalBackfillOptions.SectionName));
// CMS-0057-F API usage: which patients' data the APIs disclosed, and to which
// app, by day (Q-20). Read by the yearly usage report.
builder.Services.AddSingleton<FhirService.Services.ApiUsage.IApiDisclosureLedger,
    FhirService.Services.ApiUsage.MongoApiDisclosureLedger>();
builder.Services.AddSingleton(sp =>
{
    var limits = new FhirService.Services.Clinical.ClinicalPayloadLimits();
    builder.Configuration
        .GetSection(FhirService.Services.Clinical.ClinicalPayloadLimits.SectionName)
        .Bind(limits);
    return limits;
});
builder.Services.AddSingleton<FhirService.Services.Clinical.ClinicalPayloadValidator>();
builder.Services.AddSingleton<FhirService.Services.Clinical.ClinicalResourceProjector>();
builder.Services.AddScoped<FhirService.Services.Clinical.IClinicalResourceService,
    FhirService.Services.Clinical.ClinicalResourceService>();

// Migration: makes clinical data already held from earlier Payer-to-Payer
// exchanges readable, without asking an operator to re-run those exchanges.
// Disabled by default, idempotent, and never on a request path.
builder.Services.AddScoped<FhirService.Services.Clinical.ClinicalBackfillService>();
builder.Services.AddHostedService<FhirService.Services.Clinical.ClinicalBackfillWorker>();
// Redirects are NOT followed: a peer must not be able to bounce an outbound
// Payer-to-Payer call onto another host. TLS validation is left at the platform
// default — it is never relaxed.
builder.Services.AddHttpClient(
        FhirService.Services.PayerToPayer.Outbound.HttpPayerToPayerRemoteClient.HttpClientName,
        (sp, client) =>
        {
            var transport = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                FhirService.Services.PayerToPayer.Outbound.PayerToPayerTransportOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(transport.TimeoutSeconds, 1, 300));
        })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IChoFhirArtifactRegistry, ChoFhirArtifactRegistry>();

// ── Appeals FHIR adapter (PR 3) ───────────────────────────────────────────────
// FhirAppealMapper is stateless and pure. The adapter selection (HTTP
// vs mock) is driven by configuration: Appeals:UseMockAdapter=true
// (default in dev environments without an appeals-service instance)
// uses the in-memory seed data. Production wires the HTTP adapter.
builder.Services.AddSingleton<FhirAppealMapper>();
builder.Services.AddScoped<ICorrelationIdAccessor, CorrelationIdAccessor>();
builder.Services.AddTransient<TenantHeaderPropagationHandler>();
builder.Services.AddTransient<CorrelationIdPropagationHandler>();

var useMockAppealAdapter = builder.Configuration.GetValue<bool>(
    "Appeals:UseMockAdapter",
    defaultValue: builder.Environment.IsDevelopment());

if (useMockAppealAdapter)
{
    CloudHealthOffice.Infrastructure.Security.ChoSyntheticData.EnsureAllowed(
        builder.Environment, builder.Configuration, "Appeals:UseMockAdapter (MockFhirAppealAdapter)");
    builder.Services.AddSingleton<IFhirAppealAdapter, MockFhirAppealAdapter>();
}
else
{
    builder.Services.AddScoped<IFhirAppealAdapter, HttpFhirAppealAdapter>();
    builder.Services
        .AddHttpClient(HttpFhirAppealAdapter.HttpClientName, client =>
        {
            var baseUrl = builder.Configuration["Services:AppealsServiceUrl"]
                ?? "http://appeals-service.cloudhealthoffice/";
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<TenantHeaderPropagationHandler>()
        .AddHttpMessageHandler<CorrelationIdPropagationHandler>();
}

// ── Da Vinci PAS ──────────────────────────────────────────────────────────────
// PasAutoAdjudicator now receives IEnrollmentDecisionGate (Rule 0)
// and IPriorAuthRuleEngine (Rule 5) via constructor injection.
builder.Services.Configure<PasAutoAdjudicationConfig>(
    builder.Configuration.GetSection("Cms0057:PasAutoAdjudication"));
// Scoped, not singleton: once a database is configured the provider-enrollment registration
// supplies IEnrollmentDecisionGate as scoped, and a singleton consuming it is a captive
// dependency that fails service-descriptor validation at startup. Only the PAS controller
// resolves this, which is itself scoped.
builder.Services.AddScoped<IPasAutoAdjudicator, PasAutoAdjudicator>();
builder.Services.AddSingleton<PasResponseBuilder>();
// PAS $submit stores the request and its decision through authorization-service's
// internal, fhir-service-only endpoint before it answers (D-01, D-02).
builder.Services.AddScoped<FhirService.Services.Pas.IPasAuthorizationRecorder,
    FhirService.Services.Pas.HttpPasAuthorizationRecorder>();

// PAS $inquire (PAS-04). Read-only projection of the authoritative
// authorization record held by authorization-service — no second PA store and
// no inquiry-specific status.
builder.Services.AddScoped<FhirService.Services.IPriorAuthorizationStore,
    FhirService.Services.HttpPriorAuthorizationStore>();
builder.Services.AddScoped<FhirService.Services.IPriorAuthorizationInquiryService,
    FhirService.Services.PriorAuthorizationInquiryService>();
// D-26 caller binding. Absent configuration the patient binding observes and
// refuses nothing; the provider binding needs no setting because it applies
// only where a trusted issuer declared ServesProviderCallers.
builder.Services.Configure<FhirService.Services.PasCallerBindingOptions>(
    builder.Configuration.GetSection(FhirService.Services.PasCallerBindingOptions.SectionName));

// ── Da Vinci CDex additional information (PAS-07) ─────────────────────────────
// The request half is a Task projection served by TaskController; the response
// half is $submit-attachment in CdexController. Both read and write the ONE
// additional-information record held by rfai-service — there is no second store.
builder.Services.AddSingleton<FhirService.Services.Cdex.CdexTaskMapper>();
builder.Services.AddScoped<FhirService.Services.Cdex.ICdexAdditionalInformationStore,
    FhirService.Services.Cdex.HttpCdexAdditionalInformationStore>();
builder.Services.AddScoped<FhirService.Services.Cdex.ICdexAttachmentSubmissionService,
    FhirService.Services.Cdex.CdexAttachmentSubmissionService>();

// Submitted documentation goes into the platform's EXISTING secure attachment
// abstraction (IClaimAttachmentContentStore), which already owns checksumming,
// content-type normalisation, storage-key derivation and file-name
// sanitisation — no second storage story for prior-auth documents.
//
// LIMITATION, stated plainly: the implementation registered here is the
// process-local one. A deployment MUST bind the blob-backed implementation of
// IClaimAttachmentContentStore (the shared abstraction is shaped for
// IDocumentStore / Azure Blob) before submitted documentation is durable across
// a restart. TryAdd, so a host that registers one wins.
builder.Services.Configure<CloudHealthOffice.Infrastructure.Gateways.ClaimAttachmentOptions>(
    builder.Configuration.GetSection("Cms0057:CdexAttachments"));
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<CloudHealthOffice.Infrastructure.Gateways.IClaimAttachmentContentStore>(
        builder.Services,
        sp =>
        {
            // Through the configured logging pipeline, so the warning reaches the
            // same sinks and levels as everything else a container emits.
            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger("FhirService.Cdex.Attachments")
                .LogWarning(
                    "CDex attachments use the in-process attachment content store — submitted "
                    + "documentation does NOT survive a restart. Register a durable "
                    + "IClaimAttachmentContentStore for deployment.");

            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<
                CloudHealthOffice.Infrastructure.Gateways.ClaimAttachmentOptions>>()?.Value
                ?? new CloudHealthOffice.Infrastructure.Gateways.ClaimAttachmentOptions();

            if (string.Equals(options.ContentContainer, "claim-attachments", StringComparison.Ordinal))
                options.ContentContainer = FhirService.Services.Cdex.CdexAttachmentPolicy.StorageContainer;

            return new CloudHealthOffice.Infrastructure.Gateways.InMemoryClaimAttachmentContentStore(options);
        });

// Malware scanning is a SEAM, not an implementation: CHO has no scanning
// infrastructure, and the default scanner scans nothing and says so. A
// submission it has not scanned is recorded with scan status Unknown rather
// than Safe, so nothing downstream can mistake "not scanned" for "clean".
// Register a real IAttachmentContentScanner for deployment.
builder.Services.AddSingleton<FhirService.Services.Cdex.IAttachmentContentScanner,
    FhirService.Services.Cdex.UnscannedAttachmentContentScanner>();

// ── HTTP clients ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient(UpstreamClientNames.AuthorizationService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:AuthorizationServiceUrl"]
            ?? "http://authorization-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    // The same client also fetches the prior-authorization ExplanationOfBenefit
    // projection (G-31), which authorization-service serves as FHIR.
    client.DefaultRequestHeaders.Add("Accept", "application/fhir+json");
})
// Without the tenant header authorization-service falls back to its default
// partition, so a PAS $inquire would read (and a $submit would write) the wrong
// tenant's authorizations. Correlation follows the same call for tracing.
.AddHttpMessageHandler<TenantHeaderPropagationHandler>()
.AddHttpMessageHandler<CorrelationIdPropagationHandler>();
builder.Services.AddHttpClient(
    FhirService.Services.Cdex.HttpCdexAdditionalInformationStore.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:RfaiServiceUrl"]
            ?? "http://rfai-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
// The store sends the authenticated tenant on every call explicitly; the
// correlation id follows the same call for tracing.
.AddHttpMessageHandler<CorrelationIdPropagationHandler>();
builder.Services.AddHttpClient("TerminologyService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
            ?? "http://terminology-service.cloudhealthoffice:5010/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
// integrity-score (PasController $submit, ProviderDirectoryController) takes the
// tenant from the token, so the caller's authenticated tenant goes on every
// call: a CHO caller's token is forwarded, a SMART caller's is swapped for
// fhir-service's service token for that tenant (SmartCallerOutboundHandler).
builder.Services.AddHttpClient("ProviderVerificationService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderVerificationServiceUrl"]
            ?? "http://provider-verification-service.cloudhealthoffice:5020/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(5);
})
.AddHttpMessageHandler<TenantHeaderPropagationHandler>()
.AddHttpMessageHandler<CorrelationIdPropagationHandler>();
builder.Services.AddHttpClient("NppesApi", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Nppes:BaseUrl"] ?? "https://npiregistry.cms.hhs.gov/api/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// ── provider-service FHIR proxy (capability 5.7) ────────────────────────────
// ProviderDirectoryController.ReadPractitioner / SearchPractitioners
// proxies to provider-service's /fhir/Practitioner endpoint. Tenant
// header propagation flows the caller's TenantId through so
// provider-service's TenantMiddleware sees the same context (Decision
// 5a — tenant-scoped directory). Capabilities 5.8 and 5.9 wire
// Organization and PractitionerRole through this same client.
builder.Services.AddHttpClient("ProviderService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderServiceUrl"]
            ?? "http://provider-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/fhir+json");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddHttpMessageHandler<TenantHeaderPropagationHandler>()
.AddHttpMessageHandler<CorrelationIdPropagationHandler>();

// ── benefit-plan-service FHIR proxy (capability BP 5.8) ─────────────────────
// InsurancePlanController.ReadInsurancePlan / SearchInsurancePlans
// proxies to benefit-plan-service's /fhir/InsurancePlan endpoint —
// benefit-plan-service owns the canonical CHO InsurancePlan projection
// (mirrors provider-service's ownership of Practitioner / PractitionerRole
// / Organization for the rest of the Plan-Net Provider Directory bundle).
// Tenant + correlation header propagation matches the ProviderService
// client. See docs/architecture/fhir-insuranceplan-projection.md.
builder.Services.AddHttpClient("BenefitPlanService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:BenefitPlanServiceUrl"]
            ?? "http://benefit-plan-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/fhir+json");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddHttpMessageHandler<TenantHeaderPropagationHandler>()
.AddHttpMessageHandler<CorrelationIdPropagationHandler>();

// ── claims-service FHIR proxy (capability 5.11) ─────────────────────────────
// ExplanationOfBenefitController.ReadEob / SearchEobs proxies to
// claims-service's /fhir/ExplanationOfBenefit endpoint — claims-service
// owns the canonical CHO ExplanationOfBenefit projection. Header
// propagation matches the BenefitPlanService client so claims-service's
// TenantMiddleware sees the same TenantId the FHIR caller arrived with.
// See docs/architecture/claim-fhir-projection.md.
builder.Services.AddHttpClient(UpstreamClientNames.ClaimsService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ClaimsServiceUrl"]
            ?? "http://claims-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/fhir+json");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddHttpMessageHandler<TenantHeaderPropagationHandler>()
.AddHttpMessageHandler<CorrelationIdPropagationHandler>();

// ── Da Vinci CRD / DTR / Bulk ─────────────────────────────────────────────────
builder.Services.Configure<CrdConfig>(builder.Configuration.GetSection("Cms0057:Crd"));
builder.Services.AddMemoryCache(options => options.SizeLimit = 1024);
builder.Services.AddSingleton<ICrdClassificationStore, CrdClassificationStore>();
// CRD resolves the member context from CHO's own Patient and Coverage before it
// calls the rule engine. Scoped, because IFhirDataAdapter is.
// Programs (docs/architecture/plan-configuration.md §3), read from
// tenant-service: CrdContextResolver takes the state, line of business and
// program code for a member's benefit plan from one program record.
builder.Services.AddProgramDirectoryClient(builder.Configuration);
builder.Services.AddScoped<ICrdContextResolver, CrdContextResolver>();
builder.Services.AddScoped<ICrdService, CrdService>();
builder.Services.Configure<DtrConfig>(builder.Configuration.GetSection("Cms0057:Dtr"));
// Singleton: the store ensures its indexes at construction.
builder.Services.AddSingleton<IDtrResourceStore, MongoDtrResourceStore>();
builder.Services.AddSingleton<IDtrService, DtrService>();
// Bulk Data $export (G-34, Q-18). Off by default, and then kick-off answers
// 501 exactly as before. When on, a provider exports its own attributed panel
// into the export store (BulkExport:DocumentStore:*, its own bucket) and
// downloads it back through this service.
var bulkExportOptions = builder.Configuration
    .GetSection(FhirService.Services.BulkExport.BulkExportOptions.SectionName)
    .Get<FhirService.Services.BulkExport.BulkExportOptions>() ?? new();
if (bulkExportOptions.Enabled)
{
    bulkExportOptions.Validate();
    builder.Services.Configure<FhirService.Services.BulkExport.BulkExportOptions>(
        builder.Configuration.GetSection(FhirService.Services.BulkExport.BulkExportOptions.SectionName));
    // Singleton: the store ensures its indexes at construction.
    builder.Services.AddSingleton<FhirService.Services.BulkExport.IBulkExportJobStore,
        FhirService.Services.BulkExport.MongoBulkExportJobStore>();
    builder.Services.AddSingleton<FhirService.Services.BulkExport.IBulkExportFileStore>(sp =>
        FhirService.Services.BulkExport.DocumentStoreBulkExportFileStore.Create(
            sp.GetRequiredService<IConfiguration>(),
            builder.Environment.EnvironmentName,
            sp.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddScoped<FhirService.Services.BulkExport.IBulkExportResourceSource,
        FhirService.Services.BulkExport.BulkExportResourceSource>();
    builder.Services.AddSingleton<IBulkExportService, FhirService.Services.BulkExport.DurableBulkExportService>();
    builder.Services.AddHostedService<FhirService.Services.BulkExport.BulkExportWorker>();
}
else
{
    builder.Services.AddSingleton<IBulkExportService, BulkExportService>();
}

// ── Managed FHIR store publishing (ADR 023) ──────────────────────────────────
// Off unless ManagedFhirStore:Provider names a provider. When on, finalized
// claims' ExplanationOfBenefit projections are copied into the plan's cloud
// FHIR store (Google Cloud Healthcare API today), from which the store's own
// configuration streams them to BigQuery. A provider that is named but
// incompletely configured stops start-up instead of silently not publishing.
if (builder.Services.AddChoManagedFhirStore(builder.Configuration, builder.Environment.EnvironmentName))
{
    builder.Services.AddSingleton<FhirService.Services.ManagedFhirStore.ManagedFhirStoreProjector>();
    builder.Services.AddHostedService<FhirService.Services.ManagedFhirStore.ManagedFhirStoreProjectionConsumer>();
}

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
// NOTE: fhir-service intentionally does NOT call AddCloudHealthOfficeJsonOptions().
// FHIR R4 wire format requires numeric enum coding and its own serialization
// pipeline via FhirInputFormatter/FhirOutputFormatter; applying the shared
// JsonStringEnumConverter here would break FHIR conformance.
// See docs/architecture/shared-json-options.md.
builder.Services.AddControllers(options =>
{
    options.InputFormatters.Insert(0, new FhirInputFormatter());
    options.OutputFormatters.Insert(0, new FhirOutputFormatter());

    // Provider Access authorization, registered GLOBALLY rather than per
    // controller: a new member-scoped FHIR controller is governed the moment it
    // exists, with nothing to remember to opt into. The filter itself decides
    // which requests it governs (member-scoped resource + provider-shaped
    // token), so non-Provider-Access traffic passes straight through.
    options.Filters.Add<FhirService.Services.ProviderAccess.ProviderAccessAuthorizationFilter>();
    // Adapter failures answer as FHIR: unsupported search 400, upstream outage 502 (ADR 018).
    options.Filters.Add<FhirAdapterExceptionFilter>();
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.RedisConnectionString   = builder.Configuration["Redis:ConnectionString"];
});

// Identity trust readiness: configuration valid AND every trusted issuer's
// signing keys retrievable. Reports operational trust state only — never keys,
// never discovery payloads, and never per-token outcomes, so a burst of expired
// tokens does not read as an outage.
builder.Services.AddHealthChecks()
    .AddCheck<SmartIdentityTrustHealthCheck>(
        "smart-identity-trust",
        tags: ["ready", "identity"]);
builder.Services.AddHostedService<SmartTrustWarmupHostedService>();

builder.Services.AddHttpContextAccessor();
// CORS: browser SMART apps call this service. Allowlist only
// (Cors:AllowedOrigins, default Portal:BaseUrl), no credentials,
// closed when unconfigured outside Development. See ChoCors.
builder.Services.AddChoBrowserCors(builder.Configuration, builder.Environment);

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseChoBrowserCors();
app.UseMiddleware<AdapterLabelMiddleware>();
// Authentication (CHO or SMART), tenant from the token, then [FhirAccess].
// The first UseAuthentication + FhirTenantRefusalMiddleware only give tenant
// refusals the FHIR OperationOutcome shape; authentication results are cached
// per request, and the shared TenantMiddleware inside UseChoAuthentication
// still enforces the same rule.
app.UseAuthentication();
app.UseMiddleware<FhirTenantRefusalMiddleware>();
app.UseChoAuthentication();
// SMART only: the issuer may serve the tenant, then scopes + patient binding.
app.UseMiddleware<SmartIssuerTenantMiddleware>();
app.UseMiddleware<SmartScopeEnforcementMiddleware>();
// After scope enforcement: only reads the caller was allowed to make are counted.
app.UseMiddleware<FhirService.Middleware.ApiDisclosureRecordingMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapChoHealthChecks();
app.Run();

public partial class Program { }
