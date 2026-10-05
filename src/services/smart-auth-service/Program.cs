using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using CloudHealthOffice.Infrastructure.Extensions;
using MongoDB.Driver;
using OpenIddict.Abstractions;
using CloudHealthOffice.Infrastructure.Security;
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
    builder.Configuration["MongoDb:DatabaseName"] = mongoDbName;
    builder.Services.AddChoDatabase(builder.Configuration);
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

var smartIssuer = SmartIssuer.Resolve(builder.Configuration, builder.Environment);

// Production sign-in for members and provider users (Entra External ID).
// Null when SmartAuth:ExternalLogin:Enabled is false; throws when enabled but
// incomplete or unsafe.
var externalLogin = ExternalLogin.Resolve(builder.Configuration, builder.Environment, smartIssuer);

// Token signing / encryption certificates. Required outside Development and
// Testing; null means the development certificates.
var signingCertificates = SmartCertificates.LoadSigning(builder.Configuration, builder.Environment);
var encryptionCertificates = SmartCertificates.LoadEncryption(builder.Configuration, builder.Environment);

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
        // ── Issuer ───────────────────────────────────────────────────────────
        // Fixed from configuration: token `iss`, discovery and JWKS metadata
        // never depend on the request's Host or X-Forwarded headers.
        options.SetIssuer(smartIssuer);
        options.AddEventHandler(SmartIssuer.BaseUriHandler(smartIssuer));

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
            Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess,
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
        // SmartAuth:SigningCertificates / EncryptionCertificates (Key Vault);
        // development certificates only on Development/Testing hosts. Every
        // signing certificate is published in the JWKS; the active one signs.
        options.DisableAccessTokenEncryption();
        options.AddSmartCertificates(signingCertificates, encryptionCertificates);

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

// ── CHO tokens for the admin API and launch registration ─────────────────────
// The default scheme is the CHO bearer scheme: the admin endpoints
// (/api/admin/smart/*) and POST /launch take their tenant and actor from a CHO
// token, never from a header. No default permissions: an unannotated action
// is denied. The SMART OAuth endpoints are [AllowAnonymous] and authenticate
// explicitly (sign-in cookie, OpenIddict), exactly as before.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);

// ── Cookie auth for the consent/login UI ─────────────────────────────────────
// Not the default scheme: only the SMART sign-in flow uses it, by name.
// Secure outside Development (TLS ends at the ingress, so the request this
// pod sees is plain HTTP; the cookie is marked Secure regardless). Lax: the
// external login returns on a top-level GET.
var sessionAuthentication = builder.Services.AddAuthentication()
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(2);
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

// The external OIDC login signs in to the cookie scheme above, with exactly
// the session the development login produces (SmartSession).
if (externalLogin is not null)
{
    sessionAuthentication.AddExternalLogin(externalLogin, builder.Environment);
    builder.Services.AddSingleton(externalLogin);
}

// One Data Protection key ring for every pod (MongoDB), so a session cookie
// or an external sign-in started on one pod is honoured by another. Keys are
// encrypted at rest with the active encryption certificate when configured.
if (!string.IsNullOrEmpty(mongoConnStr))
{
    var dataProtection = builder.Services.AddDataProtection().SetApplicationName("smart-auth-service");
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<IServiceProvider>((options, sp) =>
            options.XmlRepository = new MongoDataProtectionRepository(sp.GetRequiredService<IMongoDatabase>()));
    if (encryptionCertificates is not null)
    {
        dataProtection
            .ProtectKeysWithCertificate(encryptionCertificates.Active)
            .UnprotectKeysWithAnyCertificate(encryptionCertificates.All.ToArray());
    }
}

builder.Services.AddAuthorization(options =>
    options.AddPolicy(SmartAuthService.Controllers.SmartAccessTokenPolicy.Name, policy => policy
        .AddAuthenticationSchemes(OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()));

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();

// User/client → tenant bindings and EHR launch contexts. MongoDB only: they
// are the authority for every token's tenant and patient, and must survive
// restarts and be shared across pods (a launch registered through one pod is
// consumed through another, exactly once).
static IMongoDatabase RequireMongo(IServiceProvider sp) => sp.GetService<IMongoDatabase>()
    ?? throw new InvalidOperationException(
        "MongoDb:ConnectionString is required: SMART identity bindings and launch contexts are stored in MongoDB.");
builder.Services.AddSingleton<ISmartIdentityStore>(sp => new MongoSmartIdentityStore(RequireMongo(sp)));
builder.Services.AddSingleton<ILaunchContextStore>(sp =>
    new MongoLaunchContextStore(RequireMongo(sp), sp.GetRequiredService<IConfiguration>()));
builder.Services.AddScoped<SmartTokenContextResolver>();
builder.Services.AddSingleton<SmartAuthAudit>();
builder.Services.AddSingleton<SmartConsent>();
builder.Services.AddScoped<SmartAppApprovals>();

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
// CORS: browser SMART apps call this service. Allowlist only
// (Cors:AllowedOrigins, default Portal:BaseUrl), no credentials,
// closed when unconfigured outside Development. See ChoCors.
builder.Services.AddChoBrowserCors(builder.Configuration, builder.Environment);

builder.Services.AddChoObservability(builder.Configuration);

// TLS ends at the ingress: the client's scheme comes from X-Forwarded-Proto,
// trusted only from SmartAuth:TrustedProxyNetworks. See TrustedProxy.
builder.Services.AddSmartTrustedProxy(builder.Configuration);

var app = builder.Build();

// Anti-framing, nosniff and no-referrer on every response; the login, consent
// and link pages also get a strict CSP (see SmartSecurityHeaders).
app.UseSmartSecurityHeaders();
app.UseForwardedHeaders();
app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseChoBrowserCors();

// Health checks before auth so they're accessible without a token
app.MapChoHealthChecks();

// Authentication, tenant from the CHO token (never a header), authorization.
app.UseChoAuthentication();
app.MapControllers();

app.Run();

