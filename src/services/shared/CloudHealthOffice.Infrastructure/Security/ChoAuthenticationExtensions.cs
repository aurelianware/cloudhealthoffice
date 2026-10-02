using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>Per-service authorization defaults.</summary>
public sealed class ChoAuthorizationDefaults
{
    /// <summary>Permission required by unannotated GET/HEAD actions. Null denies them.</summary>
    public string? DefaultReadPermission { get; set; }

    /// <summary>Permission required by unannotated mutating actions. Null denies them.</summary>
    public string? DefaultWritePermission { get; set; }
}

/// <summary>
/// The one way a CHO backend service authenticates callers, resolves the tenant
/// and enforces permissions.
///
/// <code>
/// builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, d =>
/// {
///     d.DefaultReadPermission  = "claims:read";
///     d.DefaultWritePermission = "claims:work";
/// });
/// ...
/// app.UseChoAuthentication();
/// app.MapControllers();
/// </code>
/// </summary>
public static class ChoAuthenticationExtensions
{
    public static IServiceCollection AddChoAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<ChoAuthorizationDefaults>? configureDefaults = null)
    {
        var options = new ChoAuthOptions();
        configuration.GetSection(ChoAuthOptions.SectionName).Bind(options);
        options.Validate(allowSymmetricKeys: AllowsSymmetricKeys(environment));

        var defaults = new ChoAuthorizationDefaults();
        configureDefaults?.Invoke(defaults);

        services.TryAddSingleton(options);
        services.TryAddSingleton(services.BuildTenantOptions());
        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentActor, HttpContextCurrentActor>();

        if (options.ServiceToken != null)
        {
            services.TryAddSingleton(ChoTokenIssuer.FromKeys(
                options.ServiceToken.Issuer, options.Audience,
                options.ServiceToken.PrivateKeyPem, options.ServiceToken.SymmetricKey,
                options.ServiceToken.Lifetime));
        }
        services.TryAddTransient<ChoOutboundTokenHandler>();

        // Every factory-built client authenticates its calls to other CHO
        // services; the handler itself refuses to attach tokens to external hosts.
        services.ConfigureHttpClientDefaults(client => client.AddChoServiceAuthentication());

        var issuersByName = options.Issuers.ToDictionary(i => i.Issuer, StringComparer.Ordinal);
        var metadataManagers = options.Issuers
            .Where(i => !string.IsNullOrWhiteSpace(i.Authority))
            .ToDictionary(
                i => i.Issuer,
                i => new ConfigurationManager<OpenIdConnectConfiguration>(
                    i.Authority!.TrimEnd('/') + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever { RequireHttps = !environment.IsDevelopment() }),
                StringComparer.Ordinal);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = !environment.IsDevelopment();
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = issuersByName.Keys.ToArray(),
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = options.ClockSkew,
                    NameClaimType = ChoClaimTypes.Name,
                    RoleClaimType = ChoClaimTypes.Role,
                    // Keys are resolved for the token's own issuer only, so one
                    // issuer's key can never verify a token naming another.
                    IssuerSigningKeyResolver = (_, token, _, _) =>
                    {
                        var iss = token.Issuer;
                        if (!issuersByName.TryGetValue(iss, out var issuer))
                            return [];
                        if (metadataManagers.TryGetValue(iss, out var manager))
                            return manager.GetConfigurationAsync(CancellationToken.None)
                                .GetAwaiter().GetResult().SigningKeys;
                        return issuer.StaticKeys();
                    },
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        var iss = ctx.Principal?.FindFirst("iss")?.Value;
                        if (iss != null && issuersByName.TryGetValue(iss, out var issuer) && issuer.AllowServiceRole
                            && ctx.Principal!.Identity is ClaimsIdentity identity)
                        {
                            identity.AddClaim(new Claim(ChoPrincipal.ServiceIssuerMarker, "true"));
                        }
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = ctx =>
                    {
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("CloudHealthOffice.Authentication")
                            .LogWarning("Bearer token rejected: {Category}", ctx.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        var passthrough = services.BuildTenantOptions().PassthroughPaths;
        services.AddAuthorization(authz =>
        {
            // Anything that reaches authorization without its own metadata
            // (minimal APIs, health/metrics middleware, unmatched routes) still
            // requires a caller, except the probe and scrape paths.
            authz.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAssertion(ctx =>
                    ctx.User.Identity?.IsAuthenticated == true
                    || (ctx.Resource is HttpContext http
                        && passthrough.Any(p => http.Request.Path.StartsWithSegments(p))))
                .Build();
        });

        services.Configure<MvcOptions>(mvc => mvc.Conventions.Add(
            new DefaultPermissionConvention(defaults.DefaultReadPermission, defaults.DefaultWritePermission)));

        return services;
    }

    /// <summary>
    /// Authentication, then tenant resolution from the token, then authorization.
    /// Probe and metrics paths pass both, so the platform can reach them.
    /// </summary>
    public static IApplicationBuilder UseChoAuthentication(this IApplicationBuilder app)
    {
        var tenantOptions = app.ApplicationServices.GetRequiredService<TenantMiddlewareOptions>();

        app.UseAuthentication();
        app.UseMiddleware<TenantMiddleware>(tenantOptions);
        app.UseAuthorization();
        return app;
    }

    private static TenantMiddlewareOptions BuildTenantOptions(this IServiceCollection services)
        => services.FirstOrDefault(d => d.ServiceType == typeof(TenantMiddlewareOptions))?.ImplementationInstance
               as TenantMiddlewareOptions
           ?? new TenantMiddlewareOptions();

    /// <summary>
    /// Symmetric keys are tolerated only where no real data lives: a Development
    /// host, or an automated test host.
    /// </summary>
    internal static bool AllowsSymmetricKeys(IHostEnvironment environment)
        => environment.IsDevelopment() || environment.IsEnvironment("Testing");
}
