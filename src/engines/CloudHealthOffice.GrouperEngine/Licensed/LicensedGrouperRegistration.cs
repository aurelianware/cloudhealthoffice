using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.GrouperEngine.Licensed;

/// <summary>
/// Where the plan's licensed APR-DRG / EAPG grouper answers. Bound from the
/// <c>LicensedGrouper</c> configuration section.
/// </summary>
public sealed class LicensedGrouperOptions
{
    public const string SectionName = "LicensedGrouper";

    /// <summary>
    /// Base URL of the plan's grouper adapter. Empty, the default, means no
    /// licensed grouper: every request answers NotConfigured.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Path of the APR-DRG endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string AprDrgPath { get; set; } = "apr-drg/v1/group";

    /// <summary>Path of the EAPG endpoint, relative to <see cref="BaseUrl"/>.</summary>
    public string EapgPath { get; set; } = "eapg/v1/group";

    /// <summary>Header that carries <see cref="ApiKey"/>.</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>
    /// Shared secret the adapter expects. Supply it from a secret store
    /// (environment variable <c>LicensedGrouper__ApiKey</c>), never from a
    /// checked-in settings file.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Seconds to wait for one answer before the claim pends as Unavailable.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Allows plain http to a host other than loopback. Requests carry
    /// diagnoses and demographics, so this is for a sidecar on a private
    /// network only.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }
}

/// <summary>
/// Registers <see cref="IAprDrgGrouper"/> and <see cref="IEapgGrouper"/>.
/// </summary>
public static class LicensedGrouperRegistration
{
    /// <summary>
    /// Binds the licensed grouper from configuration:
    /// <see cref="HttpLicensedGrouper"/> when <c>LicensedGrouper:BaseUrl</c>
    /// is set, <see cref="NotConfiguredLicensedGrouper"/> otherwise.
    ///
    /// <para>
    /// A URL that is set but unusable (not absolute, or plain http to a
    /// non-loopback host without <see cref="LicensedGrouperOptions.AllowInsecureHttp"/>)
    /// stops the host at startup. A deployment that asked for a grouper and
    /// silently got none would pend every Medicaid hospital claim with a
    /// misleading reason.
    /// </para>
    /// </summary>
    public static IServiceCollection AddLicensedGroupers(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new LicensedGrouperOptions();
        configuration.GetSection(LicensedGrouperOptions.SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            services.AddSingleton<NotConfiguredLicensedGrouper>();
            services.AddSingleton<IAprDrgGrouper>(sp => sp.GetRequiredService<NotConfiguredLicensedGrouper>());
            services.AddSingleton<IEapgGrouper>(sp => sp.GetRequiredService<NotConfiguredLicensedGrouper>());
            return services;
        }

        var baseUri = ValidateBaseUrl(options);
        if (options.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{LicensedGrouperOptions.SectionName}:TimeoutSeconds must be positive.");
        }

        services.AddSingleton(options);
        services.AddHttpClient<HttpLicensedGrouper>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddTransient<IAprDrgGrouper>(sp => sp.GetRequiredService<HttpLicensedGrouper>());
        services.AddTransient<IEapgGrouper>(sp => sp.GetRequiredService<HttpLicensedGrouper>());
        return services;
    }

    internal static Uri ValidateBaseUrl(LicensedGrouperOptions options)
    {
        var raw = options.BaseUrl.Trim();
        // A trailing slash makes the relative endpoint paths append rather than
        // replace the last segment of a base like https://host/grouper.
        if (!raw.EndsWith('/')) raw += "/";

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{LicensedGrouperOptions.SectionName}:BaseUrl must be an absolute http(s) URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !options.AllowInsecureHttp)
        {
            throw new InvalidOperationException(
                $"{LicensedGrouperOptions.SectionName}:BaseUrl uses plain http to {uri.Host}. Grouper requests "
                + "carry diagnoses and demographics; use https, or set AllowInsecureHttp for a private-network sidecar.");
        }

        return uri;
    }
}
