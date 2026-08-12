using Microsoft.Azure.Cosmos;
using BenefitPlanService.Adapters;
using BenefitPlanService.HostedServices;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.BenefitEngine.Configuration;
using CloudHealthOffice.BenefitEngine.Persistence;
using StackExchange.Redis;
using CloudHealthOffice.FeeScheduleEngine.Configuration;
using CloudHealthOffice.ClaimsScrubEngine.Configuration;
using CloudHealthOffice.NcciEngine.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.ProviderEnrollmentService.Configuration;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── Database backend ──────────────────────────────────────────────────────────
var useMongo = !string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]);

if (useMongo)
{
    builder.Services.AddSingleton<IMongoClient>(sp =>
        new MongoClient(builder.Configuration["MongoDb:ConnectionString"]));
    builder.Services.AddScoped<IMongoDatabase>(sp =>
        sp.GetRequiredService<IMongoClient>()
          .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]));
    builder.Services.AddScoped<IBenefitPlanRepository, BenefitPlanRepositoryMongo>();
    builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryMongo>();
    builder.Services.AddScoped<IPlanVersionTransitionRepository, MongoPlanVersionTransitionRepository>();
    builder.Services.AddScoped<IPlanVersionEventPublisher, MongoPlanVersionEventPublisher>();
    builder.Services.AddScoped<IPlanYearTransitionPublisher, MongoPlanYearTransitionPublisher>();
    builder.Services.AddScoped<IPlanYearScheduleSource, MongoPlanYearScheduleSource>();
    // Cosmos DB for MongoDB requires explicit indexes for ORDER BY paths.
    // Local MongoDB can otherwise conceal the missing-index incompatibility.
    builder.Services.AddSingleton<IHostedService>(sp =>
        new BenefitPlanIndexInitializer(
            sp.GetRequiredService<IMongoClient>()
              .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<BenefitPlanIndexInitializer>>()));
    builder.Services.AddSingleton<IHostedService>(sp =>
        new ServiceCategoryMappingIndexInitializer(
            sp.GetRequiredService<IMongoClient>()
              .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<ServiceCategoryMappingIndexInitializer>>()));
    // Ensures (TenantId, PlanId, EventId) and (TenantId, PlanId, Version)
    // unique indexes exist on the events collection — the publisher's
    // retry-on-duplicate loop depends on them.
    // Use a factory-based singleton (mirrors MemberEventIndexInitializer in
    // member-service) to avoid injecting the scoped IMongoDatabase into a
    // singleton hosted service.
    builder.Services.AddSingleton<IHostedService>(sp =>
        new BenefitPlanService.HostedServices.PlanVersionEventIndexInitializer(
            sp.GetRequiredService<IMongoClient>()
              .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<BenefitPlanService.HostedServices.PlanVersionEventIndexInitializer>>()));
    builder.Services.AddSingleton<IHostedService>(sp =>
        new BenefitPlanService.HostedServices.PlanYearTransitionEventIndexInitializer(
            sp.GetRequiredService<IMongoClient>()
              .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<BenefitPlanService.HostedServices.PlanYearTransitionEventIndexInitializer>>()));
    // PlanYearScheduler periodically scans plans and emits Approaching /
    // Transition events. Idempotency lives in the publisher (deterministic
    // EventId), so running on multiple replicas is safe.
    builder.Services.AddHostedService<PlanYearScheduler>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        return new CosmosClient(cfg["CosmosDb:Endpoint"], cfg["CosmosDb:Key"]);
    });
    builder.Services.AddScoped<IBenefitPlanRepository, BenefitPlanRepository>();
    builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryCosmos>();
    builder.Services.AddScoped<IPlanVersionTransitionRepository, CosmosPlanVersionTransitionRepository>();
    // Even on Cosmos, plan-version-events stream lands in Mongo today
    // (consistent with member-events pattern). Migrated to Cosmos when
    // cross-store consistency is needed — see plan-versioning.md.
    builder.Services.AddScoped<IPlanVersionEventPublisher>(sp =>
    {
        // Only register a Mongo publisher if Mongo is available; otherwise
        // fall back to a no-op so Cosmos-only deployments don't crash.
        var mongo = sp.GetService<IMongoDatabase>();
        return mongo == null
            ? new NoopPlanVersionEventPublisher(sp.GetRequiredService<ILogger<NoopPlanVersionEventPublisher>>())
            : new MongoPlanVersionEventPublisher(mongo, sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<MongoPlanVersionEventPublisher>>());
    });
    // Cosmos-only deployments get the no-op publisher and no scheduler —
    // the events stream lives in Mongo today (consistent with
    // PlanVersionEvent). See docs/architecture/plan-year-definition.md.
    builder.Services.AddScoped<IPlanYearTransitionPublisher>(sp =>
    {
        var mongo = sp.GetService<IMongoDatabase>();
        return mongo == null
            ? new NoopPlanYearTransitionPublisher(sp.GetRequiredService<ILogger<NoopPlanYearTransitionPublisher>>())
            : new MongoPlanYearTransitionPublisher(mongo, sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<MongoPlanYearTransitionPublisher>>());
    });
    Console.WriteLine("Using Cosmos DB repository");
}

// ── Redis — shared across all engines ────────────────────────────────────────
// Two registrations coexist on the same physical Redis instance:
//   1. IConnectionMultiplexer — required by RedisAccumulatorService
//      (atomic HINCRBYFLOAT on hashes) and by the SCAN-based state flush
//      inside RedisPaRuleRepository. Both are deliberate exceptions to
//      ICacheProvider — see docs/architecture/shared-cache.md.
//   2. ICacheProvider (via AddChoCaching) — tenant-config + rule-set K/V
//      consumers. AddChoCaching reuses the multiplexer above rather than
//      opening a second connection.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is required.")));

builder.Services.AddChoCaching(builder.Configuration, builder.Environment);

// ── Benefit Engine ────────────────────────────────────────────────────────────
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanServiceImpl>();
builder.Services.AddScoped<IBenefitViewService, BenefitViewService>();
builder.Services.AddHttpContextAccessor();

// ── Benefit Plan Adapters ─────────────────────────────────────────────────────
// Tenant-driven routing: each tenant can be configured to read benefit plans
// from CHO (default) or one of QNXT / Facets / HealthEdge once those adapters
// are implemented. The factory consults tenant-service config (cached 5 min by
// BenefitPlanTenantConfigCache) and falls back to "cho" on any failure.
//
// All adapters and the factory are scoped because ChoBenefitPlanAdapter wraps
// scoped business services (IBenefitPlanService / IBenefitViewService). The
// shared TTL cache lives on the singleton so it survives across requests.
builder.Services.AddHttpClient(BenefitPlanTenantConfigCache.HttpClientName)
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<BenefitPlanTenantConfigCache>();
builder.Services.AddScoped<IBenefitPlanAdapter, ChoBenefitPlanAdapter>();
builder.Services.AddScoped<IBenefitPlanAdapter, QnxtBenefitPlanAdapter>();
builder.Services.AddScoped<IBenefitPlanAdapter, FacetsBenefitPlanAdapter>();
builder.Services.AddScoped<IBenefitPlanAdapter, HealthEdgeBenefitPlanAdapter>();
builder.Services.AddScoped<BenefitPlanAdapterFactory>();
builder.Services.AddScoped<IBenefitEngineTenantContext, HttpContextTenantContext>();

builder.Services.AddHttpClient<IClaimsAccumulatorSource, ClaimsServiceAccumulatorSource>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ClaimsServiceUrl"]
        ?? throw new InvalidOperationException("Services:ClaimsServiceUrl is required."));
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddBenefitEngine().UseRedisAccumulatorService();
builder.Services.AddScoped<CloudHealthOffice.BenefitEngine.Services.IBenefitPlanProvider,
                           BenefitPlanService.Services.ChoBenefitPlanProvider>();

// ── ACA OOP Limits + Plan-Limit Validator (capability BP 5.7) ────────────────
// Loaded once at service startup; consumed by ChoBenefitPlanProvider when
// projecting BenefitPlan onto BenefitPlanConfig and by IPlanLimitValidator
// at every plan-write surface. See docs/architecture/family-accumulator-models.md.
builder.Services.Configure<AcaOopLimitsOptions>(
    builder.Configuration.GetSection(AcaOopLimitsOptions.SectionName));
builder.Services.AddSingleton<IAcaLimitsProvider, AcaLimitsProvider>();
builder.Services.AddSingleton<IPlanYearResolver, PlanYearResolver>();
builder.Services.AddScoped<IPlanLimitValidator, PlanLimitValidator>();

// ── Service-Category Mappings (capability BP 5.6) ────────────────────────────
// Replaces the prior NullServiceCategoryMappingRepository with a real Cosmos
// or Mongo backend (selected by the same MongoDb:ConnectionString switch
// that drives the BenefitPlan repository above). The same class implements
// the read seam consumed by the resolver, the write seam consumed by the
// admin controller, and the SystemDefaultsApplied idempotency record
// consumed by the seeder hosted service. See
// docs/architecture/service-category-mapping.md.
builder.Services.Configure<ServiceCategoryMappingOptions>(
    builder.Configuration.GetSection(ServiceCategoryMappingOptions.SectionName));

// Raw storage backend — Cosmos or Mongo. The same class implements all
// three seams (read, write, applied-record).
if (useMongo)
{
    builder.Services.AddScoped<ChoServiceCategoryMappingRepositoryMongo>();
}
else
{
    builder.Services.AddScoped<ChoServiceCategoryMappingRepository>();
}

// Cache decorator over the raw backend. Holds the IMemoryCache; invalidates
// on write. The decorator is what the rest of the service consumes.
builder.Services.AddScoped<CachingServiceCategoryMappingRepository>(sp =>
{
    var cache = sp.GetRequiredService<IMemoryCache>();
    var options = sp.GetRequiredService<IOptionsMonitor<ServiceCategoryMappingOptions>>();
    if (useMongo)
    {
        var inner = sp.GetRequiredService<ChoServiceCategoryMappingRepositoryMongo>();
        return new CachingServiceCategoryMappingRepository(inner, inner, inner, cache, options);
    }
    else
    {
        var inner = sp.GetRequiredService<ChoServiceCategoryMappingRepository>();
        return new CachingServiceCategoryMappingRepository(inner, inner, inner, cache, options);
    }
});
builder.Services.AddScoped<CloudHealthOffice.BenefitEngine.Services.IServiceCategoryMappingRepository>(
    sp => sp.GetRequiredService<CachingServiceCategoryMappingRepository>());
builder.Services.AddScoped<IServiceCategoryMappingWriteRepository>(
    sp => sp.GetRequiredService<CachingServiceCategoryMappingRepository>());
builder.Services.AddScoped<ISystemDefaultsAppliedRecordRepository>(
    sp => sp.GetRequiredService<CachingServiceCategoryMappingRepository>());

builder.Services.AddSingleton<SystemDefaultMappingSeeder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemDefaultMappingSeeder>());

// ── 834 Plan-Code Mappings ────────────────────────────────────────────────────
// Crosswalk from a trading partner's own 834 plan code to this platform's
// PlanId (see Enrollment834PlanCodeMapping). Mongo-only so far — every
// recently-added capability in this service has landed Mongo-only.
if (useMongo)
{
    builder.Services.AddScoped<IEnrollment834PlanCodeMappingRepository, Enrollment834PlanCodeMappingRepositoryMongo>();
}
else
{
    builder.Services.AddScoped<IEnrollment834PlanCodeMappingRepository, NullEnrollment834PlanCodeMappingRepository>();
}
builder.Services.AddFeeScheduleEngine().UseRepositoriesFromConfiguration(builder.Configuration);
builder.Services.AddClaimsScrubEngine();
builder.Services.AddNcciEngine().UseRepositoryFromConfiguration(builder.Configuration);
builder.Services.AddScoped<IAccumulatorAuditWriter, MongoAccumulatorAuditWriter>();

// ── Provider Enrollment Service ───────────────────────────────────────────────
// Supplies IEnrollmentDecisionGate → AdjudicationController.ValidateProviderEnrollment
// and the validate-provider step in the Argo adjudication workflow.
//
// PassthroughEnrollmentGate is registered first as a fallback — it is overridden
// by StateEnrollmentGate inside AddProviderEnrollmentService when Redis/DB are
// available. In test environments where infrastructure is stubbed out, the
// passthrough remains and always passes (correct test behavior).
//
// Required appsettings.json additions:
//   "ProviderEnrollmentService": {
//     "TenantConfigCacheTtlSeconds": 300,
//     "EnabledStateCodes": [],
//     "Tmhp": { "ApiKey": "...(from AKV)" },
//     "Caqh": { "Username": "...", "Password": "...(from AKV)" }
//   }
builder.Services.AddScoped<IEnrollmentDecisionGate, PassthroughEnrollmentGate>();

if (useMongo)
    builder.Services.AddProviderEnrollmentService(builder.Configuration)
        .UseMongoRepositories().WithTenantConfigCache()
        .WithTexasSource().WithCaqhSource();
else
    builder.Services.AddProviderEnrollmentService(builder.Configuration)
        .UseCosmosRepositories().WithTenantConfigCache()
        .WithTexasSource().WithCaqhSource();

// ── Prior Auth Rule Engine ────────────────────────────────────────────────────
// Supplies IPriorAuthRuleEngine → AdjudicationController (future: pre-adjudication
// PA check endpoint) and the authorization-service direct 278 path.
// Seeds TX platform rules on first deployment.
//
// Required appsettings.json additions:
//   "PriorAuthRuleEngine": {
//     "RuleSetCacheTtlMinutes": 15,
//     "GoldCardLookbackDays": 180,
//     "PendOnRuleError": true
//   }
if (useMongo)
    builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
        .UseMongoRepository().WithRuleCache()
        .WithPlatformRules().SeedOnStartup();
else
    builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
        .UseCosmosRepository().WithRuleCache()
        .WithPlatformRules().SeedOnStartup();

// ── Operating Mode Provider ───────────────────────────────────────────────────
// Fetches per-tenant operating mode configuration from tenant-service.
// Cached for 5 minutes — mode changes are admin actions, not hot-path.
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IOperatingModeProvider, HttpOperatingModeProvider>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TenantServiceUrl"]
        ?? "http://tenant-service/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ── Claim Type Router ────────────────────────────────────────────────────────
// Determines CHO vs. legacy routing per claim type and line of business.
builder.Services.AddSingleton<IClaimTypeRouter, ClaimTypeRouter>();

// ── Provider Integrity Gate (capability 5.10 — cached-or-live) ───────────────
// Adjudication-path gate that reads the canonical projection on
// Provider.IntegrityScore from provider-service by default and only falls
// back to provider-verification-service when the cached score is null,
// stale, or callers explicitly opt in via forceRefresh: true. The 1-hour
// MemoryCache stays as a per-pod request-coalescing layer wrapping the
// outer ProviderIntegrityResult. See
// docs/architecture/integrity-score-consumption.md for the canonical
// decision tree.
builder.Services.Configure<ProviderIntegrityGateOptions>(
    builder.Configuration.GetSection(ProviderIntegrityGateOptions.SectionName));
builder.Services.AddHttpClient(HttpProviderIntegrityGate.ProviderServiceClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderServiceUrl"]
        ?? "http://provider-service/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient(HttpProviderIntegrityGate.VerificationServiceClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderVerificationServiceUrl"]
        ?? "http://provider-verification-service/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IProviderIntegrityGate, HttpProviderIntegrityGate>();

// ── Prospective Adjudication / Payment Estimate Service ──────────────────────
// Provider-facing read-only claim payment estimate. Reuses the existing
// fee-schedule pricing + benefit-calculation engines in a simulation mode
// (AdjudicationExecutionMode.Prospective) so no accumulator, claim, payment,
// or workflow state is ever mutated. Consumed by EstimateController's
// POST /api/v1/adjudication/estimate. See
// docs/architecture/prospective-adjudication.md.
builder.Services.AddScoped<IPaymentEstimateService, PaymentEstimateService>();

// ── FHIR InsurancePlan Projector (capability BP 5.8) ─────────────────────────
// Stateless, hand-built JsonObject projector. Mirrors provider-service's
// FhirPractitionerProjector / FhirOrganizationProjector — no Hl7.Fhir.R4
// dependency. Consumed by FhirInsurancePlanController; fhir-service proxies
// /fhir/r4/InsurancePlan/* requests to that controller via a typed
// HttpClient("BenefitPlanService") registration on the fhir-service side.
// See docs/architecture/fhir-insuranceplan-projection.md.
builder.Services.AddSingleton<IFhirInsurancePlanProjector, FhirInsurancePlanProjector>();

// ── FHIR Endpoint Projector (capability BP 5.9) ──────────────────────────────
// Stateless, hand-built. Projects PlanDocumentReference[] from a published
// BenefitPlan into FHIR Endpoint resources (one per externally-addressable
// document). Consumed by FhirEndpointController and by
// FhirInsurancePlanProjector to populate InsurancePlan.endpoint[] with
// Reference(Endpoint/{id}) entries. See
// docs/architecture/fhir-endpoint-projection.md.
builder.Services.AddSingleton<IFhirEndpointProjector, FhirEndpointProjector>();

// ── Network-Tier → Organization Reference (5.5) ──────────────────────────────
// Read-side lookup against provider-service capability 5.3 Organization
// entity. Reuses the ProviderService HttpClient registered above. Backfill
// service + admin controller realise the operator-driven NetworkId
// mapping. See docs/architecture/network-tier-organization-reference.md.
builder.Services.AddSingleton<IOrganizationLookupClient, HttpOrganizationLookupClient>();
builder.Services.Configure<NetworkTierBackfillOptions>(
    builder.Configuration.GetSection(NetworkTierBackfillOptions.SectionName));
builder.Services.AddSingleton<INetworkTierSoftValidator, NetworkTierSoftValidator>();
builder.Services.AddScoped<INetworkTierBackfillService, NetworkTierBackfillService>();

// ── Terminology Crosswalk Client ─────────────────────────────────────────────
// Resolves plan-specific procedure code mappings before fee schedule pricing.
builder.Services.AddHttpClient<ITerminologyCrosswalkClient, HttpTerminologyCrosswalkClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
        ?? "http://terminology-service/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions()
    // Register BenefitJsonConverter here (not via [JsonConverter] on Benefit) so
    // that WithoutSelf() can reliably strip it from a copy, avoiding the
    // attribute-inheritance stack overflow on polymorphic write.
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new BenefitJsonConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var claimsServiceHealthUrl = builder.Configuration["Services:ClaimsServiceUrl"]
    ?? "http://claims-service";
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString  = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint         = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey              = builder.Configuration["CosmosDb:Key"];
    options.RedisConnectionString    = builder.Configuration["Redis:ConnectionString"];
    options.HttpDependencies["claims-service"] =
        $"{claimsServiceHealthUrl.TrimEnd('/')}/health/live";
});

builder.Services.AddCors(options => options.AddPolicy("AllowAll",
    policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();
app.Run();

public partial class Program { }
