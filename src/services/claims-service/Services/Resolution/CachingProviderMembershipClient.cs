using Microsoft.Extensions.Caching.Memory;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Decorator over <see cref="IProviderMembershipClient"/> with a 5-minute
/// in-process TTL keyed by
/// <c>(tenantId, networkId, npi, asOfDate)</c>. Mirrors
/// <see cref="CachingBenefitPlanResolver"/> and the BP 5.10
/// <c>HttpProviderIntegrityGate</c> caching shape.
///
/// <para>
/// Cache key is namespaced by resolution path
/// (<c>cached-or-live</c> vs <c>force-refresh</c>) so a force-refresh
/// call doesn't poison the default-path entry — the same convention
/// BP 5.10 uses. <c>asOfDate</c> is part of the key (rounded to whole
/// days) because the same NPI can have different membership at
/// different dates; collapsing the day boundary keeps cache keys finite
/// while preserving service-date semantics within a day.
/// </para>
///
/// <para>
/// Negative results (<c>null</c> from upstream) are NOT cached — a
/// transient provider-service outage shouldn't pin "lookup unavailable"
/// for the full TTL window. The "not a member" 404 path produces a
/// non-null <see cref="NetworkMembership"/> with
/// <c>IsActiveMember=false</c>, which IS cached normally.
/// </para>
/// </summary>
public sealed class CachingProviderMembershipClient : IProviderMembershipClient
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly IProviderMembershipClient _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingProviderMembershipClient(IProviderMembershipClient inner, IMemoryCache cache)
        : this(inner, cache, DefaultTtl) { }

    public CachingProviderMembershipClient(IProviderMembershipClient inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task<NetworkMembership?> GetMembershipAsync(
        string tenantId,
        string networkId,
        string npi,
        DateTime asOf,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, networkId, npi, asOf, forceRefresh);

        if (!forceRefresh
            && _cache.TryGetValue<NetworkMembership>(key, out var cached)
            && cached is not null)
        {
            return cached;
        }

        var fresh = await _inner
            .GetMembershipAsync(tenantId, networkId, npi, asOf, forceRefresh, ct)
            .ConfigureAwait(false);

        if (fresh is not null && _ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, _ttl);
        }
        return fresh;
    }

    internal static string BuildCacheKey(
        string tenantId, string networkId, string npi, DateTime asOf, bool forceRefresh)
    {
        var path = forceRefresh ? "force" : "cached-or-live";
        var dayKey = asOf.ToUniversalTime().Date.ToString("yyyyMMdd");
        return $"membership:{path}:{tenantId}:{networkId}:{npi}:{dayKey}";
    }
}
