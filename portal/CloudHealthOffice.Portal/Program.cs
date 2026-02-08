using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor.Services;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Hubs;
using System.Net.Http;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Override Stripe keys from environment variables (Kubernetes secrets)
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STRIPE_PUBLISHABLE_KEY")))
{
    builder.Configuration["Stripe:PublishableKey"] = Environment.GetEnvironmentVariable("STRIPE_PUBLISHABLE_KEY");
}
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY")))
{
    builder.Configuration["Stripe:SecretKey"] = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
}

// Configure forwarded headers for running behind reverse proxy/ingress
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Azure AD Authentication
var initialScopes = builder.Configuration["DownstreamApi:Scopes"]?.Split(' ') ?? Array.Empty<string>();

builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(options =>
    {
        builder.Configuration.Bind("AzureAd", options);
        
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
                context.Response.Redirect("/Error/AdminConsentRequired");
                context.HandleResponse();
            }
            
            return Task.CompletedTask;
        };
        
        // Also handle remote failure (covers more error scenarios)
        options.Events.OnRemoteFailure = context =>
        {
            var error = context.Failure?.Message ?? "";
            
            if (error.Contains("AADSTS650052") || 
                error.Contains("lacks a service principal") ||
                error.Contains("AADSTS65001")) // User/admin hasn't consented
            {
                context.Response.Redirect("/Error/AdminConsentRequired");
                context.HandleResponse();
            }
            
            return Task.CompletedTask;
        };
    })
    .EnableTokenAcquisitionToCallDownstreamApi(initialScopes)
    .AddInMemoryTokenCaches();

// Configure cookies for reverse proxy (HTTPS behind nginx)
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax; // Changed from None to Lax
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".CloudHealthOffice.Auth";
    
    // Configure for reverse proxy
    options.ForwardDefaultSelector = true;
});

builder.Services.AddAuthorization(options =>
{
    // Fallback policy requires authentication, but specific endpoints can opt-out with [AllowAnonymous]
    var policyBuilder = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser();
    options.FallbackPolicy = policyBuilder.Build();
    
    // Add anonymous policy for health checks
    options.AddPolicy("Anonymous", policy => policy.RequireAssertion(_ => true));
});

// Add services to the container
builder.Services.AddRazorPages()
    .AddMicrosoftIdentityUI();
builder.Services.AddServerSideBlazor()
    .AddMicrosoftIdentityConsentHandler();
builder.Services.AddMudServices();

// Add HttpClient for service calls
builder.Services.AddHttpClient("default")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 50
    });

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("default"));

// Register microservice clients
builder.Services.AddScoped<IMemberService, MemberService>();
builder.Services.AddScoped<ICoverageService, CoverageService>();
builder.Services.AddScoped<IClaimsService, ClaimsService>();
builder.Services.AddScoped<IEligibilityService, EligibilityService>();
builder.Services.AddScoped<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<IProviderService, ProviderService>();
builder.Services.AddScoped<IBenefitPlanService, BenefitPlanService>();
builder.Services.AddScoped<IWorkflowService, WorkflowService>();
builder.Services.AddScoped<IMetricsService, MetricsService>();
builder.Services.AddScoped<ISponsorService, SponsorService>();
builder.Services.AddScoped<IReferenceDataService, ReferenceDataService>();

// Add SignalR
builder.Services.AddSignalR();

// Add session state
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax; // Changed from None to Lax
    options.Cookie.Name = ".CloudHealthOffice.Session";
});

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

// Health endpoint - anonymous access for Kubernetes probes
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());

app.MapRazorPages();
app.MapBlazorHub();
app.MapHub<ClaimsHub>("/hubs/claims");
app.MapHub<WorkflowHub>("/hubs/workflows");
app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.svg"));
app.MapFallbackToPage("/_Host");

app.Run();
