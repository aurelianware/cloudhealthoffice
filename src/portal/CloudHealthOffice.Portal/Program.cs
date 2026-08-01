using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Server.Circuits;
using MudBlazor.Services;
using CloudHealthOffice.Portal.Infrastructure;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Hubs;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using MongoDB.Driver;
using MongoDB.Bson.Serialization.Conventions;
using CloudHealthOffice.Infrastructure.Configuration;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Override Stripe keys from environment variables (Kubernetes secrets)
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STRIPE_PUBLISHABLE_KEY")))
{
    builder.Configuration["Stripe:PublishableKey"] = Environment.GetEnvironmentVariable("STRIPE_PUBLISHABLE_KEY");
}
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY")))
{
    builder.Configuration["Stripe:SecretKey"] = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
}

// Note: Stripe__Price__Starter and Stripe__Price__Professional environment variables
// are automatically mapped to Stripe:Price:Starter and Stripe:Price:Professional
// by ASP.NET Core configuration system (__ -> :)

// Configure forwarded headers for running behind reverse proxy/ingress
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var authMode = builder.Configuration["Authentication:Mode"] ?? "Entra";
var useLocalDemoAuth = builder.Environment.IsDevelopment()
    && string.Equals(authMode, "LocalDemo", StringComparison.OrdinalIgnoreCase);

if (useLocalDemoAuth)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/local-demo/sign-in";
            options.LogoutPath = "/local-demo/sign-out";
            options.AccessDeniedPath = "/local-demo/sign-in";
            options.Cookie.Name = ".CloudHealthOffice.LocalDemoAuth";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
}
else
{
    // Azure AD Authentication
    // Do not include downstream API scopes in the initial sign-in request.
    // For multi-tenant apps, custom API scopes must be acquired incrementally
    // (on first API call) to avoid AADSTS1003031 at the authorization endpoint.
    var initialScopes = Array.Empty<string>();

    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(options =>
    {
        builder.Configuration.Bind("AzureAd", options);

        // Use 'query' response mode so the callback is a GET (not cross-origin POST)
        // This avoids SameSite cookie issues in local development
        if (builder.Environment.IsDevelopment())
        {
            options.ResponseMode = "query";
        }

        // Force HTTPS for redirect URIs when behind reverse proxy
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            // Ensure we use HTTPS scheme for redirect URIs
            if (context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
            {
                var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
                if (forwardedProto == "https")
                {
                    context.ProtocolMessage.RedirectUri = context.ProtocolMessage.RedirectUri?.Replace("http://", "https://");
                }
            }
            return Task.CompletedTask;
        };
        
        // Handle authentication failures - detect admin consent required
        options.Events.OnAuthenticationFailed = context =>
        {
            var error = context.Exception.Message;

            // Check for admin consent required error (AADSTS650052)
            if (error.Contains("AADSTS650052") || error.Contains("lacks a service principal"))
            {
                var tenantId = ExtractTenantIdFromError(error);
                var redirectUrl = BuildAdminConsentErrorUrl(tenantId);
                context.Response.Redirect(redirectUrl);
                context.HandleResponse();
            }

            return Task.CompletedTask;
        };

        // Also handle remote failure (covers more error scenarios)
        options.Events.OnRemoteFailure = context =>
        {
            var error = context.Failure?.Message ?? "";
            var queryError = context.Request.Query["error_description"].FirstOrDefault() ?? "";
            var combinedError = string.IsNullOrEmpty(queryError) ? error : queryError;

            if (error.Contains("AADSTS650052") ||
                error.Contains("lacks a service principal") ||
                error.Contains("AADSTS65001") ||
                queryError.Contains("AADSTS650052") ||
                queryError.Contains("AADSTS65001"))
            {
                var tenantId = ExtractTenantIdFromError(combinedError);
                var redirectUrl = BuildAdminConsentErrorUrl(tenantId);
                context.Response.Redirect(redirectUrl);
                context.HandleResponse();
            }

            return Task.CompletedTask;
        };
    })
    .EnableTokenAcquisitionToCallDownstreamApi(initialScopes)
    .AddDistributedTokenCaches();
}

// Configure cookies for reverse proxy (HTTPS behind nginx)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".CloudHealthOffice.Auth";
});

builder.Services.AddAuthorization(options =>
{
    // Don't use fallback policy - let pages opt-in to authentication
    // This allows [AllowAnonymous] pages like /signup and /welcome to work
    
    // Add anonymous policy for health checks and public pages
    options.AddPolicy("Anonymous", policy => policy.RequireAssertion(_ => true));
});

// Add services to the container
builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorPages()
    .AddMicrosoftIdentityUI();
builder.Services.AddServerSideBlazor(options =>
{
    // Always enable detailed errors so circuit exceptions appear in server logs.
    // The error detail is only sent to the client in Development; in Production the
    // server still logs it but the client only sees a generic message.
    options.DetailedErrors = true;
    options.DisconnectedCircuitMaxRetained = 100;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(60);
    options.MaxBufferedUnacknowledgedRenderBatches = 10;
})
    .AddMicrosoftIdentityConsentHandler();
// NOTE: Do NOT register AuthenticationStateProvider manually here.
// AddServerSideBlazor() + AddMicrosoftIdentityConsentHandler() already registers the
// correct provider (MicrosoftIdentityConsentAndErrorHandler wrapping
// ServerAuthenticationStateProvider). A second AddScoped<> call overrides it and
// drops the consent-handler wrapper, which can crash circuit startup.

// Diagnostic circuit handler — logs every circuit lifecycle event so startup
// failures are visible in pod logs without needing remote-attach debugging.
builder.Services.AddScoped<CircuitHandler, DiagnosticCircuitHandler>();

builder.Services.AddMudServices();

// Add HttpClient for service calls with tenant context
builder.Services.AddScoped<TenantHttpMessageHandler>();

builder.Services.AddHttpClient("default")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddHttpMessageHandler<TenantHttpMessageHandler>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 50
    });

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("default"));

// MongoDB client (singleton) — uses camelCase BSON convention to match stored field names
var camelCasePack = new ConventionPack { new CamelCaseElementNameConvention() };
ConventionRegistry.Register("CamelCase", camelCasePack, _ => true);

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration["MongoDB:ConnectionString"]
        ?? "mongodb://admin:securepassword123@mongodb:27017";
    return new MongoClient(connectionString);
});

// DataProtection — persist keys to MongoDB so all replicas share the same key ring.
// Without this each pod generates ephemeral keys and cannot decrypt cookies / Blazor
// circuit tokens produced by a different replica.
// NOTE: Must use AddOptions<KeyManagementOptions> to set XmlRepository directly rather
// than registering IXmlRepository in DI — AddDataProtection's internal setup does not
// reliably pick up a separately-registered IXmlRepository singleton.
builder.Services.AddDataProtection()
    .SetApplicationName("CloudHealthOffice.Portal")
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudHealthOffice", "DataProtection-Keys")));

if (!builder.Environment.IsDevelopment())
{
    // In production, persist keys to MongoDB so all replicas share the same key ring
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<IMongoClient, ILoggerFactory>((options, mongoClient, loggerFactory) =>
        {
            options.XmlRepository = new MongoDbXmlRepository(
                mongoClient,
                loggerFactory.CreateLogger<MongoDbXmlRepository>());
        });
}

// Register tenant context service (must be before other services that depend on it)
builder.Services.AddScoped<ITenantContextService, TenantContextService>();

// Register user context service for RBAC
builder.Services.AddScoped<IUserContextService, UserContextService>();

// Register microservice clients
builder.Services.AddScoped<IMemberService, MemberService>();
builder.Services.AddScoped<IMemberAlertService, MemberAlertService>();
builder.Services.AddScoped<IMemberNoteService, MemberNoteService>();
builder.Services.AddScoped<IFamilyRelationshipService, FamilyRelationshipService>();
builder.Services.AddScoped<ICoverageService, CoverageService>();
builder.Services.AddScoped<IClaimsService, ClaimsService>();
builder.Services.AddScoped<IEdiTransactionsService, EdiTransactionsService>();
builder.Services.AddScoped<IEligibilityService, EligibilityService>();
builder.Services.AddScoped<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<IMemberDocumentService, MemberDocumentService>();
builder.Services.AddScoped<IIdCardService, CloudHealthOffice.Portal.Services.IdCardService>();
builder.Services.AddScoped<IProviderService, ProviderService>();
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanService>();
builder.Services.AddScoped<IBenefitPlanValidationService, BenefitPlanValidationService>();
builder.Services.AddScoped<IWorkflowService, WorkflowService>();
builder.Services.AddScoped<IMetricsService, MetricsService>();
builder.Services.AddScoped<ISponsorService, SponsorService>();
builder.Services.AddScoped<IReferenceDataService, ReferenceDataService>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IOperatingModeService, OperatingModeService>();
builder.Services.AddSingleton<IEmailNotificationService, SmtpEmailNotificationService>();
builder.Services.AddScoped<ISalesInquiryService, SalesInquiryService>();
builder.Services.AddScoped<IEdiOperationsService, EdiOperationsService>();
builder.Services.AddScoped<IPaymentRunService, PaymentRunService>();
builder.Services.AddScoped<IPremiumBillingService, PremiumBillingService>();
builder.Services.AddScoped<IReportingService, ReportingService>();
builder.Services.AddScoped<IWorkQueueService, WorkQueueService>();
builder.Services.AddScoped<IEnrollmentOperationsService, EnrollmentOperationsService>();
builder.Services.AddScoped<IAppealsService, AppealsService>();
builder.Services.AddScoped<ICorrespondenceService, CorrespondenceService>();
builder.Services.AddScoped<IPricingApiService, PricingApiService>();
builder.Services.AddScoped<ICapitationService, CapitationService>();
builder.Services.AddScoped<IProviderContractsService, ProviderContractsService>();
builder.Services.AddScoped<IArService, ArServiceImpl>();
builder.Services.AddScoped<ITerminologyService, TerminologyServiceImpl>();

// TMPPM PA Rule query service (direct MongoDB queries for PA Rule Explorer)
builder.Services.AddSingleton<ITmppmRuleQueryService, TmppmRuleQueryService>();

// Add SignalR with tuned timeouts to reduce spurious circuit disconnects
builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.HandshakeTimeout = TimeSpan.FromSeconds(15);
    options.MaximumReceiveMessageSize = 64 * 1024; // 64 KB
    // Keep detailed errors enabled so exceptions are logged server-side.
    // Clients only see the full message in Development; in Production they
    // see a generic error, but the pod log captures the full exception.
    options.EnableDetailedErrors = true;
});

if (useLocalDemoAuth)
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    // Shared distributed cache backed by Redis — required for multi-pod session and MSAL token caches
    var redisConnection = builder.Configuration["Redis:ConnectionString"] ?? "redis-dataprotection.cloudhealthoffice.svc.cluster.local:6379";
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "cho:";
    });
}

// Add session state
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.Name = ".CloudHealthOffice.Session";
});

// Helper: extract tenant ID from Azure AD error messages
static string? ExtractTenantIdFromError(string error)
{
    // Pattern: "your organization '{tenant-id}'"
    var match = System.Text.RegularExpressions.Regex.Match(
        error, @"organization\s+'([0-9a-f\-]{36})'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value : null;
}

// Helper: build redirect URL with tenant context for admin consent error page
static string BuildAdminConsentErrorUrl(string? tenantId)
{
    var url = "/Error/AdminConsentRequired";
    if (!string.IsNullOrEmpty(tenantId))
    {
        url += $"?tenantId={Uri.EscapeDataString(tenantId)}";
    }
    return url;
}

// Register background tenant seed so it doesn't block startup or health probes.
// Local demo auth bypasses tenant subscription lookup and should not require
// MongoDB just to open the portal.
if (!useLocalDemoAuth)
{
    builder.Services.AddHostedService<TenantSeedService>();
    builder.Services.AddHostedService<TmppmIndexService>();
}

var app = builder.Build();

// Use forwarded headers FIRST (before any other middleware)
app.UseForwardedHeaders();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Don't use HTTPS redirection - ingress handles TLS termination
// app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

if (useLocalDemoAuth)
{
    app.MapGet("/local-demo/sign-in", async (HttpContext context) =>
    {
        var redirectUri = NormalizeLocalDemoRedirect(
            context.Request.Query["redirectUri"].FirstOrDefault(),
            context.Request);

        var email = builder.Configuration["Authentication:LocalDemo:Email"]
            ?? "local-demo-user";
        var name = builder.Configuration["Authentication:LocalDemo:DisplayName"]
            ?? "Local Demo Admin";
        var tenantId = builder.Configuration["Authentication:LocalDemo:TenantId"]
            ?? "demo";
        var azureTenantId = builder.Configuration["Authentication:LocalDemo:AzureTenantId"]
            ?? "local-demo";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "local-demo-admin"),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Email, email),
            new("preferred_username", email),
            new("oid", "local-demo-admin"),
            new("tid", azureTenantId),
            new("extension_TenantId", tenantId),
            new("cho_local_demo", "true"),
            new(ClaimTypes.Role, "TenantAdmin"),
            new(ClaimTypes.Role, "ClaimsSupervisor")
        };

        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });

        return Results.Redirect(redirectUri);
    }).WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());

    app.MapGet("/local-demo/sign-out", async (HttpContext context) =>
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/");
    }).WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());
}

static string NormalizeLocalDemoRedirect(string? redirectUri, HttpRequest request)
{
    if (string.IsNullOrWhiteSpace(redirectUri))
    {
        return "/";
    }

    if (redirectUri.StartsWith("/", StringComparison.Ordinal)
        && !redirectUri.StartsWith("//", StringComparison.Ordinal))
    {
        return redirectUri;
    }

    if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var absolute)
        && string.Equals(absolute.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
        && (!absolute.IsDefaultPort || absolute.Port == request.Host.Port))
    {
        return string.IsNullOrEmpty(absolute.PathAndQuery)
            ? "/"
            : absolute.PathAndQuery;
    }

    return "/";
}

// Health endpoint - anonymous access for Kubernetes probes
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());

app.MapControllers();
app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<ClaimsHub>("/hubs/claims");
app.MapHub<WorkflowHub>("/hubs/workflows");
app.MapHub<PaymentRunHub>("/hubs/paymentruns");
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.svg"));
app.MapFallbackToPage("/_Host");

app.Run();
