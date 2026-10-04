using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// CORS for the few CHO services a browser calls directly (fhir-service and
/// smart-auth-service for SMART apps, the public Pricing API). Every other CHO
/// service is called server-to-server only (the portal is Blazor Server) and
/// registers no CORS at all, so a browser on another origin gets no
/// <c>Access-Control-Allow-Origin</c> from it.
/// <para>
/// Origins come from <c>Cors:AllowedOrigins</c> (an array, or one string of
/// origins separated by commas or semicolons). Without it, the portal origin
/// (<c>Portal:BaseUrl</c>) is the only one allowed. Outside Development:
/// <list type="bullet">
///   <item>nothing configured means no origin is allowed (fail closed);</item>
///   <item><c>*</c> is refused at startup.</item>
/// </list>
/// In Development, with nothing configured, any <c>localhost</c> origin is
/// allowed; <c>*</c> is honoured there but never with credentials.
/// Credentials (cookies) are never allowed: CHO APIs take bearer tokens.
/// </para>
/// </summary>
public static class ChoCors
{
    public const string PolicyName = "ChoBrowserOrigins";
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";
    public const string PortalBaseUrlKey = "Portal:BaseUrl";

    /// <summary>The origins this configuration allows, normalised to <c>scheme://host[:port]</c>.</summary>
    public static IReadOnlyList<string> ResolveOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = ReadList(configuration, AllowedOriginsKey);
        if (configured.Count == 0 && !string.IsNullOrWhiteSpace(configuration[PortalBaseUrlKey]))
            configured = new List<string> { configuration[PortalBaseUrlKey]! };

        var origins = new List<string>();
        foreach (var raw in configured)
        {
            var value = raw.Trim();
            if (value == "*")
            {
                if (!environment.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"{AllowedOriginsKey} contains '*'. Outside Development name each origin " +
                        "(for example https://portal.cloudhealthoffice.com).");
                }
                origins.Add("*");
                continue;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0)
                || !string.IsNullOrEmpty(uri.Query))
            {
                throw new InvalidOperationException(
                    $"{AllowedOriginsKey} entry '{value}' is not an origin (scheme://host[:port], no path).");
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !environment.IsDevelopment() && !uri.IsLoopback)
            {
                throw new InvalidOperationException(
                    $"{AllowedOriginsKey} entry '{value}' is plain http. Outside Development only https origins are allowed.");
            }

            origins.Add(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        }

        return origins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Registers <see cref="PolicyName"/>. <paramref name="configure"/> can add
    /// exposed headers; it must not add origins or credentials.
    /// </summary>
    public static IServiceCollection AddChoBrowserCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<CorsPolicyBuilder>? configure = null)
    {
        var origins = ResolveOrigins(configuration, environment);

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (origins.Contains("*"))
            {
                policy.AllowAnyOrigin();
            }
            else if (origins.Count > 0)
            {
                policy.WithOrigins(origins.ToArray());
            }
            else if (environment.IsDevelopment())
            {
                policy.SetIsOriginAllowed(origin =>
                    Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
            }
            else
            {
                // Fail closed: no origin matches.
                policy.SetIsOriginAllowed(_ => false);
            }

            policy.AllowAnyMethod()
                  .AllowAnyHeader()
                  .DisallowCredentials();

            configure?.Invoke(policy);
        }));

        return services;
    }

    /// <summary>Applies <see cref="PolicyName"/>. Call after routing, before authentication.</summary>
    public static IApplicationBuilder UseChoBrowserCors(this IApplicationBuilder app)
        => app.UseCors(PolicyName);

    private static List<string> ReadList(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var children = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (children.Count > 0)
            return children;

        return (section.Value ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
