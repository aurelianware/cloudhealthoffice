using CloudHealthOffice.Infrastructure.Tenancy;

namespace ClaimsService.Adapters;

/// <summary>
/// Resolves the correct <see cref="IClaimAdapter"/> at runtime based on
/// tenant configuration. Mirrors
/// <c>ProviderService.Adapters.ProviderAdapterFactory</c> and
/// <c>BenefitPlanService.Adapters.BenefitPlanAdapterFactory</c>.
///
/// <para>
/// Tenant config is fetched from tenant-service (cached 5 minutes via
/// <see cref="ClaimTenantConfigCache"/>) and matched against each adapter's
/// <see cref="IClaimAdapter.Platform"/> property (case-insensitive). On any
/// failure or unknown platform the factory falls back to <c>"cho"</c> so a
/// flaky tenant-service never breaks claim reads.
/// </para>
/// <para>
/// A vendor platform key (<c>qnxt</c>, <c>facets</c>, <c>healthedge</c>) with no
/// registered connector is the exception: it throws
/// <see cref="CloudHealthOffice.Infrastructure.Tenancy.LiveCoreConnectorNotEnabledException"/>
/// (501) instead of serving CHO's data. Live connectors are registered only when
/// <c>CoreIntegration:LiveConnectors:Enabled</c> is true.
/// </para>
/// </summary>
public class ClaimAdapterFactory
{
    private readonly IEnumerable<IClaimAdapter> _adapters;
    private readonly ClaimTenantConfigCache _cache;
    private readonly ILogger<ClaimAdapterFactory> _logger;

    public ClaimAdapterFactory(
        IEnumerable<IClaimAdapter> adapters,
        ClaimTenantConfigCache cache,
        ILogger<ClaimAdapterFactory> logger)
    {
        _adapters = adapters;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get the claim adapter configured for the given tenant. Returns the
    /// CHO adapter when no specific configuration is found.
    /// </summary>
    public async Task<IClaimAdapter> GetAdapterAsync(string tenantId, CancellationToken ct = default)
    {
        var (platform, _) = await _cache.GetAsync(tenantId, ct);
        return ResolveAdapter(platform);
    }

    /// <summary>
    /// Get the adapter and a copy of the tenant's platform-settings
    /// dictionary (callers may mutate the copy without affecting the cache).
    /// </summary>
    public async Task<(IClaimAdapter Adapter, Dictionary<string, string> Settings)> GetAdapterWithSettingsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var (platform, settings) = await _cache.GetAsync(tenantId, ct);
        return (ResolveAdapter(platform), new Dictionary<string, string>(settings));
    }

    private IClaimAdapter ResolveAdapter(string platform)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));

        if (adapter == null)
        {
            // A vendor platform with no registered connector is refused, never
            // answered with CHO's own data (ADR 027). Live connectors are off
            // unless CoreIntegration:LiveConnectors:Enabled is true.
            if (LiveCoreConnectors.IsVendorPlatform(platform))
            {
                throw new LiveCoreConnectorNotEnabledException("claims", platform);
            }

            _logger.LogWarning(
                "No claim adapter found for platform '{Platform}', falling back to 'cho'",
                platform);
            adapter = _adapters.First(a =>
                string.Equals(a.Platform, ClaimTenantConfigCache.DefaultPlatform, StringComparison.OrdinalIgnoreCase));
        }

        return adapter;
    }
}
