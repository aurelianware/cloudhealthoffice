namespace ProviderService.Adapters;

/// <summary>
/// Resolves the correct <see cref="IOrganizationAdapter"/> at runtime
/// from tenant configuration. Reuses <see cref="ProviderTenantConfigCache"/>
/// — networks live in provider-service and share the same
/// <c>providerPlatform</c> tenant config block, so the cache (and its
/// 5-minute TTL) is shared across both adapter families.
/// </summary>
public class OrganizationAdapterFactory
{
    private readonly IEnumerable<IOrganizationAdapter> _adapters;
    private readonly ProviderTenantConfigCache _cache;
    private readonly ILogger<OrganizationAdapterFactory> _logger;

    public OrganizationAdapterFactory(
        IEnumerable<IOrganizationAdapter> adapters,
        ProviderTenantConfigCache cache,
        ILogger<OrganizationAdapterFactory> logger)
    {
        _adapters = adapters;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IOrganizationAdapter> GetAdapterAsync(string tenantId, CancellationToken ct = default)
    {
        var (platform, _) = await _cache.GetAsync(tenantId, ct);
        return ResolveAdapter(platform);
    }

    public async Task<(IOrganizationAdapter Adapter, Dictionary<string, string> Settings)> GetAdapterWithSettingsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var (platform, settings) = await _cache.GetAsync(tenantId, ct);
        return (ResolveAdapter(platform), new Dictionary<string, string>(settings));
    }

    private IOrganizationAdapter ResolveAdapter(string platform)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));

        if (adapter == null)
        {
            _logger.LogWarning(
                "No organization adapter found for platform '{Platform}', falling back to 'cho'",
                platform);
            adapter = _adapters.First(a =>
                string.Equals(a.Platform, ProviderTenantConfigCache.DefaultPlatform, StringComparison.OrdinalIgnoreCase));
        }

        return adapter;
    }
}
