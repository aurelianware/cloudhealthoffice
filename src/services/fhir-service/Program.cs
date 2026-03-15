using FhirService.Formatters;
using FhirService.Middleware;
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

app.UseHttpsRedirection();
app.UseCors("AllowAll");

// JWT validation must run before SMART scope enforcement
app.UseAuthentication();

// TenantMiddleware: extracts CHO tenant context from JWT or header
app.UseMiddleware<TenantMiddleware>();

// SmartScopeEnforcementMiddleware: enforces SMART scopes and patient binding
// Runs after authentication so User.Claims are populated
app.UseMiddleware<SmartScopeEnforcementMiddleware>();

app.UseAuthorization();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

// Exposed for WebApplicationFactory in integration tests
public partial class Program { }
