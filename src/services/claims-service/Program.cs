using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Extensions;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using ClaimsService.Adapters;
using ClaimsService.EDI.Florida;
using ClaimsService.Fhir;
using ClaimsService.HostedServices;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Messaging;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Migrations;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.ClaimsScrubEngine.Configuration;
using CloudHealthOffice.NcciEngine.Configuration;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Shared infrastructure: health checks, CORS, Swagger, database, tenant middleware
builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName = "Claims Service";
    options.ServiceDescription = "Healthcare claims processing for Cloud Health Office. " +
                                 "Handles 837 claim submission, 835 remittance, 277 status updates, and adjudication results.";
});

// Repository — Cosmos or Mongo based on config (database client registered by shared lib)
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];

if (!string.IsNullOrEmpty(mongoConnectionString))
{
    builder.Services.AddScoped<IClaimRepository, ClaimRepositoryMongo>();
    builder.Services.AddHostedService<ClaimIndexInitializer>();
    builder.Services.AddScoped<IAiExaminationAuditRepository, AiExaminationAuditRepositoryMongo>();
    builder.Services.AddScoped<IMassAdjudicationRunRepository, MassAdjudicationRunRepositoryMongo>();
    builder.Services.AddScoped<IClaimImportTransactionRepository, ClaimImportTransactionRepositoryMongo>();
    builder.Services.AddHostedService<MassAdjudicationRunIndexInitializer>();

    // Claim version event publisher (5.1) — Mongo append-only stream is the
    // system-of-record for the version chain. Mirrors
    // MongoProviderVersionEventPublisher / MongoPlanVersionEventPublisher.
    builder.Services.AddScoped<IClaimVersionEventPublisher, MongoClaimVersionEventPublisher>();
    builder.Services.AddScoped<IClaimVersionEventReader, MongoClaimVersionEventReader>();
    builder.Services.AddHostedService<ClaimVersionEventIndexInitializer>();

    // 5.12a — ClaimAdjustment aggregate persistence (Mongo per Gap 4
    // ratification). Indexes (chain-uniqueness for depth=1, idempotency,
    // status+createdAt for ReversalRun batch queries) are created once
    // at startup by ClaimAdjustmentIndexInitializer so scoped repository
    // resolution stays side-effect free.
    builder.Services.AddScoped<IClaimAdjustmentRepository, ClaimAdjustmentRepositoryMongo>();
    builder.Services.AddHostedService<ClaimAdjustmentIndexInitializer>();
}
else
{
    var cosmosEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    var cosmosKey = builder.Configuration["CosmosDb:Key"];
    var cosmosConnectionString = builder.Configuration["CosmosDb:ConnectionString"];

    if (string.IsNullOrEmpty(cosmosConnectionString) &&
        (string.IsNullOrEmpty(cosmosEndpoint) || string.IsNullOrEmpty(cosmosKey)))
    {
        throw new InvalidOperationException(
            "Claims Service requires a database. Configure either MongoDb:ConnectionString " +
            "or CosmosDb:ConnectionString (or CosmosDb:Endpoint + CosmosDb:Key).");
    }

    builder.Services.AddScoped<IClaimRepository, ClaimRepository>();
    builder.Services.AddScoped<IAiExaminationAuditRepository, AiExaminationAuditRepositoryCosmos>();
    builder.Services.AddSingleton<IMassAdjudicationRunRepository, InMemoryMassAdjudicationRunRepository>();
    builder.Services.AddSingleton<IClaimImportTransactionRepository, InMemoryClaimImportTransactionRepository>();

    // 5.1b — Cosmos partition-key migration tooling. Resolves the source
    // (legacy /memberId Bicep / /Id runtime) and target (canonical
    // /tenantId) containers from the configured database; the migration
    // service is the only consumer that ever sees both containers.
    // Singleton because it owns the running-flag + last-run state shared
    // across requests; the underlying Cosmos containers are thread-safe.
    builder.Services.Configure<ClaimMigrationOptions>(
        builder.Configuration.GetSection(ClaimMigrationOptions.SectionName));
    builder.Services.AddSingleton<IClaimMigrationContainerResolver, CosmosClaimMigrationContainerResolver>();
    builder.Services.AddSingleton<IClaimMigrationService, ClaimMigrationService>();

    // Cosmos-only deployments don't have a provisioned events stream; the
    // Noop publisher logs a warning so ops can spot the missing wiring
    // without breaking the lifecycle path.
    builder.Services.AddScoped<IClaimVersionEventPublisher, NoopClaimVersionEventPublisher>();
    builder.Services.AddScoped<IClaimVersionEventReader, NoopClaimVersionEventReader>();

    // 5.12a — Cosmos-only deployments throw on adjustment writes per
    // Gap 4 ratification. Reads return null/empty; the noop is fail-loud
    // on writes so the missing capability surfaces immediately rather
    // than silently dropping audit data.
    builder.Services.AddScoped<IClaimAdjustmentRepository, ClaimAdjustmentRepositoryCosmosNoop>();
}

// FHIR R4 ExplanationOfBenefit projector — hand-built JsonObject to avoid
// the Hl7.Fhir.R4 transitive dep; used by the v1 member-scoped claims endpoint.
builder.Services.AddSingleton<IExplanationOfBenefitProjector, ExplanationOfBenefitProjector>();

// 277CA acknowledgment generator
builder.Services.AddScoped<IClaimAcknowledgmentService, ClaimAcknowledgmentService>();
builder.Services.AddScoped<IDiagnosisDescriptionLookup, DiagnosisDescriptionLookup>();
builder.Services.AddScoped<IClaimDiagnosisMetadataEnricher, ClaimDiagnosisMetadataEnricher>();

// 5.10 — claim finalization (Approved/PartiallyPaid → Paid). Owns the
// idempotent Paid transition with version-event chain advancement and
// Kafka claims.finalized.v1 emission. Backed by the existing
// IClaimRepository / IClaimVersionEventPublisher / IClaimEventPublisher
// triple — no new infrastructure surfaces. The
// ClaimsController.ProcessRemittance endpoint delegates here for
// non-zero-payment remittances; zero-payment Denied transitions stay
// on the legacy direct-write path until 5.12.
builder.Services.AddScoped<IClaimFinalizationService, ClaimFinalizationService>();

// 5.12a — Adjustment workflow service. Owns the supersession transition,
// invokes IClaimSubmissionService for re-adjudication, emits
// ClaimVersionSuperseded + ClaimVersionReversed events. Lifecycle ratified
// by Decision 18: AwaitingReadjudication → PendingReversal → Active.
// 5.12b's payment-service ReversalRunService consumes the resulting
// PendingReversal rows.
builder.Services.AddScoped<IClaimAdjustmentService, ClaimAdjustmentService>();

// Inter-service HTTP clients
builder.Services.AddHttpClient("ProviderService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderService"]
        ?? "http://provider-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient("ReferenceDataService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ReferenceDataService"]
        ?? "http://reference-data-service:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

builder.Services.AddHttpClient(UpstreamClientNames.TerminologyService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
        ?? builder.Configuration["Services:TerminologyService"]
        ?? "http://terminology-service");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

// FL FMMIS encounter submission pipeline
builder.Services.AddScoped<IProviderService, HttpProviderService>();
builder.Services.AddScoped<ITenantComplianceConfigService, HttpTenantComplianceConfigService>();
builder.Services.AddScoped<FmmisClaimTransformer>();
builder.Services.AddScoped<FmmisFileBuilder>();

// FL SMMC 3.0 MPIP rate enhancement
builder.Services.AddScoped<IMpipRateClient, MpipRateClient>();
builder.Services.AddScoped<IMpipAdjudicationEnhancer, MpipAdjudicationEnhancer>();

// Claim lifecycle event publisher (Kafka). Singleton + IHostedService so the
// underlying producer is initialized at app start and cleanly shut down.
// Always registered: when Kafka:BootstrapServers is unset the publisher runs
// in degraded mode and logs a warning, so dev/test environments without Kafka
// remain functional.
builder.Services.AddSingleton<ClaimEventPublisher>();
builder.Services.AddSingleton<IClaimEventPublisher>(sp => sp.GetRequiredService<ClaimEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClaimEventPublisher>());

// Claim adapter pattern (5.2 — tenant-routed claims backends). Cache is
// singleton (TTL across requests); adapters and factory are scoped because
// the CHO adapter wraps the scoped IClaimRepository. Tenant-service HTTP
// client uses a 5-second timeout so a flaky tenant-service can't stall claim
// reads — the cache falls back to "cho" on any failure.
builder.Services.AddHttpClient(ClaimTenantConfigCache.HttpClientName)
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<ClaimTenantConfigCache>();
builder.Services.AddScoped<IClaimAdapter, ChoClaimAdapter>();
builder.Services.AddScoped<IClaimAdapter, QnxtClaimAdapter>();
builder.Services.AddScoped<IClaimAdapter, FacetsClaimAdapter>();
builder.Services.AddScoped<IClaimAdapter, HealthEdgeClaimAdapter>();
builder.Services.AddScoped<ClaimAdapterFactory>();

// Canonical claim submission orchestration (5.3). Wraps the
// adapter call with structural validation and ClaimVersionSubmitted
// event emission. Both POST /api/v1/claims and the deprecated
// legacy POST /api/claims route through this single seam so the
// version-event chain has no gaps.
//
// 5.5 modification: ClaimSubmissionService also emits a
// ClaimVersionSubmittedMessage onto the claim-version-events Service
// Bus topic so the adjudication orchestrator picks it up.
builder.Services.AddScoped<IClaimSubmissionService, ClaimSubmissionService>();

// ─── Capability 5.5 — adjudication pipeline ────────────────────────
// Service Bus messaging shared abstraction. Resolves to InMemory in
// Development / when no connection string is configured. In production
// requires Messaging:ServiceBusConnectionString.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

// IMemoryCache backs the resolution decorators. Not previously
// registered in claims-service — see the 5.5 plan, drift C.
builder.Services.AddMemoryCache();

// Per-tenant pipeline configuration. Phase 1 is service-wide; per-tenant
// override is deferred to Phase 2.
builder.Services.Configure<AdjudicationPipelineOptions>(
    builder.Configuration.GetSection(AdjudicationPipelineOptions.SectionName));

// 5.6 enforcement posture (network membership + credentialing). Phase 1
// is service-wide; per-tenant override deferred to Phase 2 alongside
// the pipeline options.
builder.Services.Configure<TenantEnforcementPolicyOptions>(
    builder.Configuration.GetSection(TenantEnforcementPolicyOptions.SectionName));

var benefitPlanServiceTimeoutSeconds = Math.Clamp(
    builder.Configuration.GetValue<int?>("Services:BenefitPlanServiceTimeoutSeconds") ?? 5,
    1,
    300);

// Resolution clients — typed HttpClient + caching decorator. The benefit-plan
// client also backs the adjudication calculate-benefits shim, so benchmark
// runners can raise this timeout without changing normal fail-fast defaults.
builder.Services.AddHttpClient(HttpBenefitPlanResolver.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:BenefitPlanService"]
        ?? "http://benefit-plan-service:8080");
    client.Timeout = TimeSpan.FromSeconds(benefitPlanServiceTimeoutSeconds);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
// Both resolver layers are stateless/thread-safe. Singleton lifetime lets the
// caching decorator coalesce concurrent first reads for a newly published
// plan across claim-processing scopes in this replica.
builder.Services.AddSingleton<HttpBenefitPlanResolver>();
builder.Services.AddSingleton<IBenefitPlanResolver>(sp =>
    new CachingBenefitPlanResolver(
        sp.GetRequiredService<HttpBenefitPlanResolver>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

builder.Services.AddHttpClient(HttpMemberResolver.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:MemberService"]
        ?? "http://member-service:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpMemberResolver>();
builder.Services.AddScoped<IMemberResolver>(sp =>
    new CachingMemberResolver(
        sp.GetRequiredService<HttpMemberResolver>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// BenefitCalculationEngine — HTTP shim against benefit-plan-service's
// /api/v1/adjudication/calculate-benefits endpoint. The engine ships as
// a class library (BP 5.10) but its host-side collaborators
// (IBenefitPlanProvider, IAccumulatorService) are wired in
// benefit-plan-service against benefit-plan-service's data stores.
// Standing them up inside claims-service would mean importing the
// entire plan + accumulator data layer — that's a Phase 2 split. The
// HTTP shim consumes the canonical engine through the same surface
// portal/preview features already use.
builder.Services.AddScoped<
    CloudHealthOffice.BenefitEngine.Services.IBenefitCalculationEngine,
    HttpBenefitCalculationEngineClient>();

// Scoped tenant context — the orchestrator pins the tenant id on this
// holder before stages run so the HTTP shim can send X-Tenant-ID
// downstream from a background Service Bus subscription that has no
// HttpContext. Scoped lifetime keeps each orchestrator run isolated.
builder.Services.AddScoped<IAdjudicationTenantContext, AdjudicationTenantContext>();

// 5.6 — enforcement clients (network membership + credentialing status)
// against provider-service. Reuses the existing ProviderService named
// HttpClient registration above. Each client gets a caching decorator;
// TTLs differ by domain (5-min membership vs 1-hour credentialing).
builder.Services.AddScoped<HttpProviderMembershipClient>();
builder.Services.AddScoped<IProviderMembershipClient>(sp =>
    new CachingProviderMembershipClient(
        sp.GetRequiredService<HttpProviderMembershipClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

builder.Services.AddScoped<HttpCredentialingStatusClient>();
builder.Services.AddScoped<ICredentialingStatusClient>(sp =>
    new CachingCredentialingStatusClient(
        sp.GetRequiredService<HttpCredentialingStatusClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// ProviderIntegrityStage — federal exclusion check via benefit-plan-service's
// provider-integrity endpoint. No caching decorator here: HttpProviderIntegrityGate
// already caches on the benefit-plan-service side (1-hour IMemoryCache,
// never-fail-open contract); a second claims-service-side cache would just
// add staleness risk without a real latency win, since the upstream is
// already fast on a cache hit.
builder.Services.AddScoped<IProviderIntegrityClient, HttpProviderIntegrityClient>();

// 5.4 — Claims Scrub Engine (class library). Default standard rule set;
// per-tenant rule overrides remain a Phase 2 surface.
builder.Services.AddClaimsScrubEngine();

// 5.7 — NCCI / MUE engine (class library). Auto-detect repository binds
// to whichever backend AddChoInfrastructure registered (IMongoDatabase
// when MongoDb:ConnectionString is set; CosmosClient otherwise). Seed
// data is operator-controlled (Phase 1 — engine ScrubAsync is
// graceful when a tenant's table is empty, surfacing zero failures
// with telemetry rather than throwing).
builder.Services.AddNcciEngine().UseRepositoryFromConfiguration(builder.Configuration);

// 5.8 — CobEngine services. Pure-calculation, stateless, no I/O —
// Singleton lifetime is correct (plan Decision 1b: no AddCobEngine
// extension exists; direct DI is the established convention for this
// engine). ICobCalculationService registers but is unused in 5.8 stage
// logic — Phase 2 priorEob work exercises it for CHO-secondary
// calculation. CoordinationOfBenefitsStage exercises only
// IPayerOrderService for audit-trail rule labelling.
builder.Services.AddSingleton<
    CloudHealthOffice.CobEngine.Services.ICobCalculationService,
    CloudHealthOffice.CobEngine.Services.CobCalculationService>();
builder.Services.AddSingleton<
    CloudHealthOffice.CobEngine.Services.IPayerOrderService,
    CloudHealthOffice.CobEngine.Services.PayerOrderService>();

// 5.8 — CoverageService HTTP client + cached resolution-client triple.
// 5-minute TTL mirrors network-membership (coverage records can
// terminate without explicit signal — open-enrollment loss, mid-year
// term — vs. credentialing's 1-hour TTL where transitions are explicit
// audit-trailed events). 5-second timeout mirrors the other resolution
// clients so a flaky coverage-service can't stall the pipeline.
builder.Services.AddHttpClient(UpstreamClientNames.CoverageService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:CoverageService"]
        ?? "http://coverage-service:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpCoverageClient>();
builder.Services.AddScoped<ICoverageClient>(sp =>
    new CachingCoverageClient(
        sp.GetRequiredService<HttpCoverageClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// Coverage-to-plan resolver — lets a claim that arrives without a
// BenefitPlanId (the X12 837 on-ramp; see X12837ClaimMapper) still find the
// member's active plan before BenefitCalculationStage runs. Reuses the same
// CoverageService named HttpClient as HttpCoverageClient above.
builder.Services.AddScoped<HttpCoverageResolver>();
builder.Services.AddScoped<ICoverageResolver>(sp =>
    new CachingCoverageResolver(
        sp.GetRequiredService<HttpCoverageResolver>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

// Prior-authorization validation. The stage treats lookup degradation as
// "not enough evidence to deny" while honoring known invalid auth responses.
builder.Services.AddHttpClient(UpstreamClientNames.AuthorizationService, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:AuthorizationService"]
        ?? "http://authorization-service");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpAuthorizationValidationClient>();
builder.Services.AddScoped<IAuthorizationValidationClient, HttpAuthorizationValidationClient>();

// Stages — registered as IEnumerable<IClaimAdjudicationStage>. Capabilities
// 5.4-5.9 each replace one stub registration with the real stage that
// wraps the corresponding engine. 5.4/5.6/5.7/5.8/5.9 swap the registration
// in place rather than going through services.RemoveAll<>() — the stub
// never shipped to production so there's nothing to remove. 6/6 pipeline
// stages real after 5.9. ProviderIntegrityStage (Order=150) added later,
// closing a gap the original 5.5 stage scope never covered — see the
// stage's own doc comment and docs/architecture/claim-adjudication-pipeline.md.
builder.Services.AddScoped<IClaimAdjudicationStage, ScrubbingStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, ProviderIntegrityStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, NetworkCredentialingStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, BenefitCalculationStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, NcciEditsStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, CoordinationOfBenefitsStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, AiExaminationStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, PersistenceStage>();

builder.Services.AddScoped<IClaimAdjudicationOrchestrator, ClaimAdjudicationOrchestrator>();

var adjudicationMaxConcurrentCalls = Math.Max(
    1,
    builder.Configuration.GetValue<int?>("Messaging:AdjudicationMaxConcurrentCalls") ?? 16);

// Subscription hosted service — the orchestrator's Service Bus
// trigger. Factory shape defers subscription creation to ExecuteAsync
// so the bus is fully initialised before we Subscribe.
builder.Services.AddHostedService(sp =>
    new SubscriptionHostedService(
        services => services.GetRequiredService<IMessageBus>().Subscribe<ClaimVersionSubmittedMessage>(
            ClaimVersionEventTopics.TopicName,
            async (msg, ctx, ct) =>
            {
                using var scope = services.CreateScope();
                var orchestrator = scope.ServiceProvider
                    .GetRequiredService<IClaimAdjudicationOrchestrator>();
                await orchestrator.AdjudicateAsync(msg, ctx, ct);
            },
            new SubscriptionOptions(
                MaxConcurrentCalls: adjudicationMaxConcurrentCalls,
                SubscriptionName: ClaimVersionEventTopics.AdjudicationSubscriptionName,
                RequiredProperties: new Dictionary<string, string>
                {
                    [ClaimVersionEventTopics.MessageTypeProperty] = ClaimVersionMessageTypes.Submitted
                })),
        sp,
        sp.GetRequiredService<ILogger<SubscriptionHostedService>>()));

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Shared middleware pipeline: exception handling, Swagger (dev), tenant middleware, CORS, health checks
app.UseChoInfrastructure(builder.Configuration);

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

app.Run();

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
