using Microsoft.Azure.Cosmos;
using BenefitPlanService.Middleware;
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
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.ProviderEnrollmentService.Configuration;
using CloudHealthOffice.ProviderEnrollmentService.Gates;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Configuration;
using BenefitPlanService.Services;

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
    Console.WriteLine("Using Cosmos DB repository");
}

// ── Redis — shared across all engines ────────────────────────────────────────
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is required.")));

// ── Benefit Engine ────────────────────────────────────────────────────────────
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanServiceImpl>();
builder.Services.AddHttpContextAccessor();
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
        .UseMongoRepositories().WithRedisTenantConfigCache()
        .WithTexasSource().WithCaqhSource();
else
    builder.Services.AddProviderEnrollmentService(builder.Configuration)
        .UseCosmosRepositories().WithRedisTenantConfigCache()
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
        .UseMongoRepository().WithRedisRuleCache()
        .WithPlatformRules().SeedOnStartup();
else
    builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
        .UseCosmosRepository().WithRedisRuleCache()
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

// ── Provider Integrity Gate ──────────────────────────────────────────────────
// Checks provider against OIG/LEIE/SAM.gov exclusion lists via
// provider-verification-service. Cached 1 hour per NPI.
builder.Services.AddHttpClient<IProviderIntegrityGate, HttpProviderIntegrityGate>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderVerificationServiceUrl"]
        ?? "http://provider-verification-service:8080/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

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
builder.Services.AddControllers();
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

var app = builder.Build();

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
