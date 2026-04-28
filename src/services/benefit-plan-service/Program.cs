using Microsoft.Azure.Cosmos;
using BenefitPlanService.Adapters;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
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
builder.Services.AddScoped<CloudHealthOffice.BenefitEngine.Services.IServiceCategoryMappingRepository,
                           BenefitPlanService.Services.NullServiceCategoryMappingRepository>();
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
        ?? "http://tenant-service:8080/");
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
        ?? "http://provider-service:8080/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient(HttpProviderIntegrityGate.VerificationServiceClientName, client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderVerificationServiceUrl"]
        ?? "http://provider-verification-service:8080/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IProviderIntegrityGate, HttpProviderIntegrityGate>();

// ── Terminology Crosswalk Client ─────────────────────────────────────────────
// Resolves plan-specific procedure code mappings before fee schedule pricing.
builder.Services.AddHttpClient<ITerminologyCrosswalkClient, HttpTerminologyCrosswalkClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
        ?? "http://terminology-service:8080/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var claimsServiceHealthUrl = builder.Configuration["Services:ClaimsServiceUrl"]
    ?? "http://claims-service:8080";
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
