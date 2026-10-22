// REFERENCE ONLY — not compiled. MongoDB is the only store; the real
// src/services/benefit-plan-service/Program.cs is authoritative.

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
using CloudHealthOffice.ProviderEnrollmentService.Configuration;  // ← NEW
using CloudHealthOffice.PriorAuthRuleEngine.Configuration;         // ← NEW

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── Database backend ──────────────────────────────────────────────────────────
var mongoConnectionString = builder.Configuration["MongoDb:ConnectionString"];
if (string.IsNullOrEmpty(mongoConnectionString))
    throw new InvalidOperationException(
        "Benefit Plan Service requires a database. Configure MongoDb:ConnectionString.");

builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(mongoConnectionString));
builder.Services.AddScoped<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>()
      .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]));
builder.Services.AddScoped<IBenefitPlanRepository, BenefitPlanRepositoryMongo>();
builder.Services.AddScoped<IAccumulatorRepository, AccumulatorRepositoryMongo>();

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
builder.Services.AddFeeScheduleEngine()
    .UseMongoRepositories()
    // ADR 016 phase 4: a no-op until a deployment names a tenant for the
    // dual-read window. Inside it, claims price twice and pay on legacy.
    .UseDualReadContracts(builder.Configuration);
builder.Services.AddClaimsScrubEngine();
builder.Services.AddNcciEngine().UseMongoRepository();
builder.Services.AddScoped<IAccumulatorAuditWriter, MongoAccumulatorAuditWriter>();

// ── Provider Enrollment Service ───────────────────────────────────────────────
// Supplies IEnrollmentDecisionGate → AdjudicationController.ValidateProviderEnrollment
// and the validate-provider step in the Argo adjudication workflow.
//
// Required appsettings.json additions:
//   "ProviderEnrollmentService": {
//     "TenantConfigCacheTtlSeconds": 300,
//     "EnabledStateCodes": [],
//     "Tmhp": { "ApiKey": "...(from AKV)" },
//     "Caqh": { "Username": "...", "Password": "...(from AKV)" }
//   }
builder.Services.AddProviderEnrollmentService(builder.Configuration)
    .UseMongoRepositories().WithRedisTenantConfigCache()
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
builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
    .UseMongoRepository().WithRedisRuleCache()
    .WithPlatformRules().SeedOnStartup();

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var claimsServiceHealthUrl = builder.Configuration["Services:ClaimsServiceUrl"]
    ?? "http://claims-service:8080";
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString  = builder.Configuration["MongoDb:ConnectionString"];
    options.RedisConnectionString    = builder.Configuration["Redis:ConnectionString"];
    options.HttpDependencies["claims-service"] =
        $"{claimsServiceHealthUrl.TrimEnd('/')}/health/live";
});

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

var app = builder.Build();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();
app.Run();

public partial class Program { }
