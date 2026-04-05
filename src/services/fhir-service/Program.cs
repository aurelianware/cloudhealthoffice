using FhirService.Formatters;
using FhirService.Middleware;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using CloudHealthOffice.Infrastructure.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// ── SMART JWT Bearer authentication ──────────────────────────────────────────
// Validates tokens issued by smart-auth-service using OIDC discovery.
// smart-auth-service uses DisableAccessTokenEncryption() so tokens are standard
// RS256-signed JWTs discoverable via /.well-known/openid-configuration.
var smartIssuer = builder.Configuration["SmartAuth:Issuer"]
    ?? throw new InvalidOperationException("SmartAuth:Issuer is required.");
var smartAudience = builder.Configuration["SmartAuth:Audience"] ?? "fhir-api";
var requireHttps = builder.Configuration.GetValue<bool>("SmartAuth:RequireHttpsMetadata", true);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // OIDC discovery: fetches signing keys from {Issuer}/.well-known/openid-configuration
        options.Authority = smartIssuer;
        options.Audience = smartAudience;
        options.RequireHttpsMetadata = requireHttps;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = smartIssuer,
            ValidateAudience = true,
            ValidAudience = smartAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

// Allow scope-based authorization policies
builder.Services.AddAuthorization();

// ── FHIR data adapter ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IFhirDataAdapter, MockFhirDataAdapter>();
builder.Services.AddSingleton<FhirBundleBuilder>();
builder.Services.AddSingleton<IPatientAccessDataProvider, MockPatientAccessDataProvider>();
builder.Services.AddSingleton<ICms0057ComplianceChecker, Cms0057ComplianceChecker>();

// ── Da Vinci PAS auto-adjudication ───────────────────────────────────────────
builder.Services.Configure<PasAutoAdjudicationConfig>(
    builder.Configuration.GetSection("Cms0057:PasAutoAdjudication"));
builder.Services.AddSingleton<IPasAutoAdjudicator, PasAutoAdjudicator>();
builder.Services.AddSingleton<PasResponseBuilder>();

// ── Authorization service HTTP client (used by PAS auto-adjudicator) ─────────
builder.Services.AddHttpClient("AuthorizationService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:AuthorizationServiceUrl"]
            ?? "http://authorization-service.cloudhealthoffice/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// ── Da Vinci CRD (Coverage Requirements Discovery) ──────────────────────────
builder.Services.Configure<CrdConfig>(
    builder.Configuration.GetSection("Cms0057:Crd"));
builder.Services.AddSingleton<ICrdService, CrdService>();

// ── Terminology Service HTTP client (used by CRD for SNOMED→CPT/ICD) ────────
builder.Services.AddHttpClient("TerminologyService", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:TerminologyServiceUrl"]
            ?? "http://terminology-service.cloudhealthoffice:5010/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// ── Provider Directory: typed HttpClient for NPPES API ────────────────────────
builder.Services.AddHttpClient("NppesApi", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Nppes:BaseUrl"] ?? "https://npiregistry.cms.hhs.gov/api/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Insert FHIR formatters first so they take priority over default System.Text.Json
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
});
builder.Services.AddHttpContextAccessor();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors("AllowAll");

// JWT validation must run before SMART scope enforcement
app.UseAuthentication();

// SmartScopeEnforcementMiddleware: enforces SMART scopes and patient binding.
// Must run before TenantMiddleware so that unauthenticated requests receive a
// FHIR OperationOutcome 401 (not a plain-JSON tenant error).
app.UseMiddleware<SmartScopeEnforcementMiddleware>();

// TenantMiddleware: extracts CHO tenant context from JWT claim or header.
// Runs after scope enforcement so context.User is already validated.
app.UseMiddleware<TenantMiddleware>();

app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Exposed for WebApplicationFactory in integration tests
public partial class Program { }
