using FhirService.Formatters;
using FhirService.Middleware;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.ProviderEnrollmentService.Configuration;
using CloudHealthOffice.PriorAuthRuleEngine.Configuration;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── SMART JWT Bearer ──────────────────────────────────────────────────────────
var smartIssuer   = builder.Configuration["SmartAuth:Issuer"]
    ?? throw new InvalidOperationException("SmartAuth:Issuer is required.");
var smartAudience = builder.Configuration["SmartAuth:Audience"] ?? "fhir-api";
var requireHttps  = builder.Configuration.GetValue<bool>("SmartAuth:RequireHttpsMetadata", true);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority                 = smartIssuer;
        options.Audience                  = smartAudience;
        options.RequireHttpsMetadata      = requireHttps;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer   = true,  ValidIssuer   = smartIssuer,
            ValidateAudience = true,  ValidAudience = smartAudience,
            ValidateLifetime = true,  ClockSkew     = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// ── Shared infrastructure ─────────────────────────────────────────────────────
// Redis — shared by ProviderEnrollmentService and PriorAuthRuleEngine caches
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Redis:ConnectionString is required.")));

var useMongo = !string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]);

if (useMongo)
{
    builder.Services.AddSingleton<IMongoClient>(_ =>
        new MongoClient(builder.Configuration["MongoDb:ConnectionString"]));
    builder.Services.AddScoped<IMongoDatabase>(sp =>
        sp.GetRequiredService<IMongoClient>()
          .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]));
}
else
{
    builder.Services.AddSingleton<CosmosClient>(_ =>
        new CosmosClient(
            builder.Configuration["CosmosDb:Endpoint"],
            builder.Configuration["CosmosDb:Key"]));
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
if (useMongo)
    builder.Services.AddProviderEnrollmentService(builder.Configuration)
        .UseMongoRepositories().WithRedisTenantConfigCache()
        .WithTexasSource().WithCaqhSource();
else
    builder.Services.AddProviderEnrollmentService(builder.Configuration)
        .UseCosmosRepositories().WithRedisTenantConfigCache()
        .WithTexasSource().WithCaqhSource();

// ── Prior Auth Rule Engine ────────────────────────────────────────────────────
// Supplies IPriorAuthRuleEngine → PasAutoAdjudicator Rule 5.
// Rule sets cached in Redis (15 min TTL, invalidated on admin write).
// Seeds TX platform rules (STAR / STARPlus / STARKids) on first deployment.
//
// Required appsettings.json:
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

// ── FHIR data adapters ────────────────────────────────────────────────────────
builder.Services.AddSingleton<IFhirDataAdapter, MockFhirDataAdapter>();
builder.Services.AddSingleton<FhirBundleBuilder>();
builder.Services.AddSingleton<IPatientAccessDataProvider, MockPatientAccessDataProvider>();
builder.Services.AddSingleton<ICms0057ComplianceChecker, Cms0057ComplianceChecker>();

// ── Da Vinci PAS ──────────────────────────────────────────────────────────────
// PasAutoAdjudicator now receives IEnrollmentDecisionGate (Rule 0)
// and IPriorAuthRuleEngine (Rule 5) via constructor injection.
builder.Services.Configure<PasAutoAdjudicationConfig>(
    builder.Configuration.GetSection("Cms0057:PasAutoAdjudication"));
builder.Services.AddSingleton<IPasAutoAdjudicator, PasAutoAdjudicator>();
builder.Services.AddSingleton<PasResponseBuilder>();

// ── HTTP clients ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("AuthorizationService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:AuthorizationServiceUrl"]
            ?? "http://authorization-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("TerminologyService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
            ?? "http://terminology-service.cloudhealthoffice:5010/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddHttpClient("ProviderVerificationService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:ProviderVerificationServiceUrl"]
            ?? "http://provider-verification-service.cloudhealthoffice:5020/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("NppesApi", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Nppes:BaseUrl"] ?? "https://npiregistry.cms.hhs.gov/api/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// ── Da Vinci CRD / DTR / Bulk ─────────────────────────────────────────────────
builder.Services.Configure<CrdConfig>(builder.Configuration.GetSection("Cms0057:Crd"));
builder.Services.AddSingleton<ICrdService, CrdService>();
builder.Services.Configure<DtrConfig>(builder.Configuration.GetSection("Cms0057:Dtr"));
builder.Services.AddSingleton<IDtrService, DtrService>();
builder.Services.AddSingleton<IBulkExportService, BulkExportService>();

// ── ASP.NET Core ──────────────────────────────────────────────────────────────
builder.Services.AddControllers(options =>
{
    options.InputFormatters.Insert(0, new FhirInputFormatter());
    options.OutputFormatters.Insert(0, new FhirOutputFormatter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.RedisConnectionString   = builder.Configuration["Redis:ConnectionString"];
    options.CosmosDbEndpoint        = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey             = builder.Configuration["CosmosDb:Key"];
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddCors(options => options.AddPolicy("AllowAll",
    p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseMiddleware<SmartScopeEnforcementMiddleware>();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();
app.Run();

public partial class Program { }
