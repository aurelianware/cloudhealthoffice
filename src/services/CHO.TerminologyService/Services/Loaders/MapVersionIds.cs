using CHO.TerminologyService.Services;

namespace CHO.TerminologyService.Services.Loaders;

public static class MapVersionIds
{
    /// <summary>
    /// The map version id, which also prefixes every entry id of the load.
    /// A tenant's override load names the tenant (length-prefixed, so no tenant
    /// and map name can combine into another's) plus a random suffix: two
    /// tenants loading the same file in the same second used to get the same
    /// version and entry ids, and the second load failed on duplicate keys (or,
    /// with a matching filter, wrote into the other tenant's ids). Global loads
    /// keep their shape.
    /// </summary>
    public static string For(MapLoadOptions options)
    {
        var stamp = $"{options.MapName}-{options.Version}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        if (!options.IsOverride)
            return stamp;

        var tenant = options.TenantId ?? string.Empty;
        return $"override:{tenant.Length}:{tenant}:{stamp}-{Guid.NewGuid():N}";
    }
}
