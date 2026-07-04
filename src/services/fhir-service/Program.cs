using FhirService.Formatters;
using FhirService.Middleware;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.ProviderEnrollmentService.Configuration;
using CloudHealthOffice.PriorAuthRuleEngine.Configuration;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

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
// ICacheProvider (via AddChoCaching) backs the tenant-config and rule-set
// K/V caches. On the Redis backend path it also registers a shared
// IConnectionMultiplexer in DI so RedisPaRuleRepository's SCAN-based
// state-flush — a deliberate exception to ICacheProvider — can inject
// it. See docs/architecture/shared-cache.md.
builder.Services.AddChoCaching(builder.Configuration, builder.Environment);

var useMongo = !string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]);

if (useMongo)
{
    builder.Services.AddSingleton<IMongoClient>(_ =>
        new MongoClient(builder.Configuration["MongoDb:ConnectionString"]));
    builder.Services.AddScoped<IMongoDatabase>(sp =>
        sp.GetRequiredService<IMongoClient>()
          .GetDatabase(builder.Configuration["MongoDb:DatabaseName"]));
}
else if (!string.IsNullOrEmpty(builder.Configuration["CosmosDb:Endpoint"]))
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
var hasDb = useMongo || !string.IsNullOrEmpty(builder.Configuration["CosmosDb:Endpoint"]);
// Cache backend presence is decided by AddChoCaching (Redis when a
// connection string is configured AND env is Production; InMemory
// otherwise). Either resolves to a working ICacheProvider, so the
// engine wiring no longer hinges on "hasRedis" — only on "hasDb".
if (hasDb)
{
    if (useMongo)
        builder.Services.AddProviderEnrollmentService(builder.Configuration)
            .UseMongoRepositories().WithTenantConfigCache()
            .WithTexasSource().WithCaqhSource();
    else
        builder.Services.AddProviderEnrollmentService(builder.Configuration)
            .UseCosmosRepositories().WithTenantConfigCache()
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
    if (useMongo)
        builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
            .UseMongoRepository().WithRuleCache()
            .WithPlatformRules().SeedOnStartup();
    else
        builder.Services.AddPriorAuthRuleEngine(builder.Configuration)
            .UseCosmosRepository().WithRuleCache()
            .WithPlatformRules().SeedOnStartup();
}
else
{
    // Local dev / test without Redis+DB: register passthrough implementations
    // so DI resolution of PasAutoAdjudicator doesn't fail.
    builder.Services.AddSingleton<CloudHealthOffice.ProviderEnrollmentService.Abstractions.IEnrollmentDecisionGate,
        CloudHealthOffice.ProviderEnrollmentService.Gates.PassthroughEnrollmentGate>();
    builder.Services.AddSingleton<CloudHealthOffice.PriorAuthRuleEngine.Abstractions.IPriorAuthRuleEngine,
        FhirService.Services.NoOpPriorAuthRuleEngine>();
}

// ── FHIR data adapters ────────────────────────────────────────────────────────
builder.Services.AddSingleton<IFhirDataAdapter, MockFhirDataAdapter>();
builder.Services.AddSingleton<FhirBundleBuilder>();
builder.Services.AddSingleton<IPatientAccessDataProvider, MockPatientAccessDataProvider>();
builder.Services.AddSingleton<ICms0057ComplianceChecker, Cms0057ComplianceChecker>();
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
builder.Services.AddScoped<ICrdService, CrdService>();
builder.Services.Configure<DtrConfig>(builder.Configuration.GetSection("Cms0057:Dtr"));
builder.Services.AddSingleton<IDtrService, DtrService>();
builder.Services.AddSingleton<IBulkExportService, BulkExportService>();

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

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

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
