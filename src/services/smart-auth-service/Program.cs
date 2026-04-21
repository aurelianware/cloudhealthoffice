using Microsoft.AspNetCore.Authentication.Cookies;
using MongoDB.Driver;
using OpenIddict.Abstractions;
using SmartAuthService.Middleware;
using SmartAuthService.Services;
using SmartAuthService.Workers;
using static OpenIddict.Abstractions.OpenIddictConstants;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// ── MongoDB ───────────────────────────────────────────────────────────────────
var mongoConnStr = builder.Configuration["MongoDb:ConnectionString"];
var mongoDbName = builder.Configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";

if (!string.IsNullOrEmpty(mongoConnStr))
{
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnStr));
    builder.Services.AddSingleton<IMongoDatabase>(sp =>
        sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDbName));
    Console.WriteLine("OpenIddict: using MongoDB token/application store");
}
else
{
    // Fallback: in-memory MongoDB via EphemeralMongoDatabase (dev/test only)
    Console.WriteLine("OpenIddict: MongoDb:ConnectionString not set — using in-memory stores");
}

// ── OpenIddict authorization server ──────────────────────────────────────────
var accessTokenLifetime = TimeSpan.FromMinutes(
    builder.Configuration.GetValue<int>("SmartAuth:AccessTokenLifetimeMinutes", 60));

var refreshTokenLifetime = TimeSpan.FromDays(
    builder.Configuration.GetValue<int>("SmartAuth:RefreshTokenLifetimeDays", 7));

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        if (!string.IsNullOrEmpty(mongoConnStr))
        {
            options.UseMongoDb()
                   .UseDatabase(builder.Services
                       .BuildServiceProvider()
                       .GetRequiredService<IMongoDatabase>());
        }
        else
        {
            // Dev/test: in-memory stores (no MongoDB required)
            // Replace with .UseMongoDb() in production
        }
    })
    .AddServer(options =>
    {
        // ── Endpoints ────────────────────────────────────────────────────────
        options
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .SetIntrospectionEndpointUris("/connect/introspect")
            .SetEndSessionEndpointUris("/connect/logout")
            .SetUserInfoEndpointUris("/connect/userinfo");

        // ── Flows ────────────────────────────────────────────────────────────
        options
            .AllowAuthorizationCodeFlow()
                .RequireProofKeyForCodeExchange()   // PKCE enforced for public clients
            .AllowRefreshTokenFlow()
            .AllowClientCredentialsFlow();          // system/*.read for backends

        // ── Token lifetimes ──────────────────────────────────────────────────
        options
            .SetAccessTokenLifetime(accessTokenLifetime)
            .SetRefreshTokenLifetime(refreshTokenLifetime);
        // Sliding expiration enabled by default; each use issues a new refresh token

        // ── SMART R4 scopes ──────────────────────────────────────────────────
        options.RegisterScopes(
            Scopes.OpenId, Scopes.Profile, Scopes.Email,
            "fhirUser",
            "launch",
            "launch/patient",
            "launch/encounter",
            "patient/*.read",
            "user/*.read",
            "system/*.read",
            "patient/Patient.read",
            "patient/Coverage.read",
            "patient/ExplanationOfBenefit.read",
            "patient/Encounter.read",
            "patient/Claim.read",
            "user/Patient.read",
            "user/Coverage.read",
            "user/ExplanationOfBenefit.read",
            "user/Encounter.read",
            "user/Claim.read",
            "system/Patient.read",
            "system/Coverage.read",
            "system/ExplanationOfBenefit.read",
            "system/Encounter.read",
            "system/Claim.read"
        );

        // ── Token signing ────────────────────────────────────────────────────
        // Disable access token encryption so standard JwtBearer can validate them.
        // Production: replace development certs with Azure Key Vault certificates.
        options.DisableAccessTokenEncryption();
        options
            .AddDevelopmentEncryptionCertificate()
            .AddDevelopmentSigningCertificate();

        // ── ASP.NET Core integration ─────────────────────────────────────────
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableEndSessionEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableStatusCodePagesIntegration();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// ── Cookie auth for the consent/login UI ─────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<LaunchContextStore>();
builder.Services.AddSingleton<ILaunchContextStore>(sp => sp.GetRequiredService<LaunchContextStore>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LaunchContextStore>());
builder.Services.AddHttpContextAccessor();

// ── Hosted seed worker ────────────────────────────────────────────────────────
builder.Services.AddHostedService<OpenIddictSeedWorker>();

// ── MVC + Swagger ─────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});
builder.Services.AddCors(options =>
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseCors("AllowAll");

// Health checks before auth so they're accessible without a token
app.MapChoHealthChecks();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantMiddleware>();
app.MapControllers();

app.Run();

