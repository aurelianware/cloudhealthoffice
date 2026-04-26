namespace BenefitPlanService.Adapters;

/// <summary>
/// Resolves the correct <see cref="IBenefitPlanAdapter"/> at runtime based on
/// tenant configuration. Mirrors <c>EligibilityService.Adapters.EligibilityAdapterFactory</c>.
///
/// <para>
/// Tenant config is fetched from tenant-service (cached 5 minutes via
/// <see cref="BenefitPlanTenantConfigCache"/>) and matched against each adapter's
/// <see cref="IBenefitPlanAdapter.Platform"/> property (case-insensitive).
/// On any failure or unknown platform the factory falls back to <c>"cho"</c>
/// so a flaky tenant-service never breaks plan reads.
/// </para>
/// </summary>
public class BenefitPlanAdapterFactory
{
    private readonly IEnumerable<IBenefitPlanAdapter> _adapters;
    private readonly BenefitPlanTenantConfigCache _cache;
    private readonly ILogger<BenefitPlanAdapterFactory> _logger;

    public BenefitPlanAdapterFactory(
        IEnumerable<IBenefitPlanAdapter> adapters,
        BenefitPlanTenantConfigCache cache,
        ILogger<BenefitPlanAdapterFactory> logger)
    {
        _adapters = adapters;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Get the benefit-plan adapter configured for the given tenant.
    /// Returns the CHO adapter when no specific configuration is found.
    /// </summary>
    public async Task<IBenefitPlanAdapter> GetAdapterAsync(string tenantId, CancellationToken ct = default)
    {
        var (platform, _) = await _cache.GetAsync(tenantId, ct);
        return ResolveAdapter(platform);
    }

    /// <summary>
    /// Get the adapter and a copy of the tenant's platform-settings dictionary
    /// (callers may mutate the copy without affecting the cache).
    /// </summary>
    public async Task<(IBenefitPlanAdapter Adapter, Dictionary<string, string> Settings)> GetAdapterWithSettingsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var (platform, settings) = await _cache.GetAsync(tenantId, ct);
        return (ResolveAdapter(platform), new Dictionary<string, string>(settings));
    }

    private IBenefitPlanAdapter ResolveAdapter(string platform)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));

        if (adapter == null)
        {
            _logger.LogWarning(
                "No benefit-plan adapter found for platform '{Platform}', falling back to 'cho'",
                platform);
            adapter = _adapters.First(a =>
                string.Equals(a.Platform, BenefitPlanTenantConfigCache.DefaultPlatform, StringComparison.OrdinalIgnoreCase));
        }

        return adapter;
    }
}
