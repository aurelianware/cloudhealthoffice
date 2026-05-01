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
using ClaimsService.Services.Resolution;

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
    builder.Services.AddScoped<IAiExaminationAuditRepository, AiExaminationAuditRepositoryMongo>();

    // Claim version event publisher (5.1) — Mongo append-only stream is the
    // system-of-record for the version chain. Mirrors
    // MongoProviderVersionEventPublisher / MongoPlanVersionEventPublisher.
    builder.Services.AddScoped<IClaimVersionEventPublisher, MongoClaimVersionEventPublisher>();
    builder.Services.AddHostedService<ClaimVersionEventIndexInitializer>();
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

    // Cosmos-only deployments don't have a provisioned events stream; the
    // Noop publisher logs a warning so ops can spot the missing wiring
    // without breaking the lifecycle path.
    builder.Services.AddScoped<IClaimVersionEventPublisher, NoopClaimVersionEventPublisher>();
}

// FHIR R4 ExplanationOfBenefit projector — hand-built JsonObject to avoid
// the Hl7.Fhir.R4 transitive dep; used by the v1 member-scoped claims endpoint.
builder.Services.AddSingleton<IExplanationOfBenefitProjector, ExplanationOfBenefitProjector>();

// 277CA acknowledgment generator
builder.Services.AddScoped<IClaimAcknowledgmentService, ClaimAcknowledgmentService>();

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

// Resolution clients — typed HttpClient + caching decorator. 5-second
// timeout matches ClaimTenantConfigCache and HttpProviderService so a
// flaky downstream service can't stall the pipeline.
builder.Services.AddHttpClient(HttpBenefitPlanResolver.HttpClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:BenefitPlanService"]
        ?? "http://benefit-plan-service:8080");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddScoped<HttpBenefitPlanResolver>();
builder.Services.AddScoped<IBenefitPlanResolver>(sp =>
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

// Stages — registered as IEnumerable<IClaimAdjudicationStage>. Capabilities
// 5.4-5.9 replace the stub registrations via services.RemoveAll<>()
// + AddScoped<IClaimAdjudicationStage, RealStage>(). 5.6 wires the
// real NetworkCredentialingStage directly without going through the
// remove/re-add dance because the stub never made it past 5.5; the
// pattern in the comment remains the convention for 5.4 / 5.7-5.9.
builder.Services.AddScoped<IClaimAdjudicationStage, ScrubbingStubStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, NetworkCredentialingStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, BenefitCalculationStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, NcciEditsStubStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, CoordinationOfBenefitsStubStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, AiExaminationStubStage>();
builder.Services.AddScoped<IClaimAdjudicationStage, PersistenceStage>();

builder.Services.AddScoped<IClaimAdjudicationOrchestrator, ClaimAdjudicationOrchestrator>();

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
            new SubscriptionOptions(SubscriptionName: ClaimVersionEventTopics.AdjudicationSubscriptionName)),
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
