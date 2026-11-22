using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Infrastructure.Tenancy;

/// <summary>
/// The switch for live core connectors: the per-request adapters that call a
/// plan's core admin system (QNXT, Facets, HealthEdge HealthRules Payer).
///
/// <para>
/// The default way a plan's core feeds CHO is the daily file drop
/// (docs/integration/CPCDS-FILE-DROP-IMPORTER.md): the core writes extracts,
/// CHO loads them, and every CMS-0057-F API reads what was loaded. Live
/// connectors are an optional, separately scoped integration point for a plan
/// that would rather build them (docs/integration/CORE-INTEGRATION-OPTIONS.md,
/// ADR 027). They are off by default, so a deployment registers only the CHO
/// adapters unless <see cref="ConfigKey"/> is <c>true</c>.
/// </para>
///
/// <para>
/// When they are off, a tenant whose configuration names a vendor platform is
/// refused with <see cref="LiveCoreConnectorNotEnabledException"/> (501), never
/// served CHO's own data in its place.
/// </para>
/// </summary>
public static class LiveCoreConnectors
{
    /// <summary>Configuration key. Environment variable: <c>CoreIntegration__LiveConnectors__Enabled</c>.</summary>
    public const string ConfigKey = "CoreIntegration:LiveConnectors:Enabled";

    /// <summary>The platform keys that name a vendor core rather than CHO.</summary>
    public static readonly IReadOnlySet<string> VendorPlatforms =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qnxt", "facets", "healthedge" };

    /// <summary>True when the deployment has opted in to live core connectors.</summary>
    public static bool IsEnabled(IConfiguration configuration)
        => bool.TryParse(configuration[ConfigKey], out var enabled) && enabled;

    /// <summary>True when <paramref name="platform"/> names a vendor core.</summary>
    public static bool IsVendorPlatform(string? platform)
        => !string.IsNullOrWhiteSpace(platform) && VendorPlatforms.Contains(platform.Trim());
}

/// <summary>
/// A tenant names a vendor core platform for a domain, and no live connector
/// for it is registered in this deployment. Derives from
/// <see cref="NotImplementedException"/> so the shared exception middleware
/// answers 501, the same status an unimplemented connector method gives.
/// </summary>
public sealed class LiveCoreConnectorNotEnabledException : NotImplementedException
{
    public LiveCoreConnectorNotEnabledException(string domain, string platform)
        : base(
            $"The tenant's {domain} platform is '{platform}', a live core connector, and live core " +
            $"connectors are not enabled in this deployment ({LiveCoreConnectors.ConfigKey}). " +
            "Load the core's data through the file drop instead, or see " +
            "docs/integration/CORE-INTEGRATION-OPTIONS.md.")
    {
        Domain = domain;
        Platform = platform;
    }

    /// <summary>The domain whose platform was configured (claims, providers, ...).</summary>
    public string Domain { get; }

    /// <summary>The vendor platform key the tenant names.</summary>
    public string Platform { get; }
}
