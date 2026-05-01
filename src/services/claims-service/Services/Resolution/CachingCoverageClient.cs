using Microsoft.Extensions.Caching.Memory;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Decorator over <see cref="ICoverageClient"/> with a 5-minute in-process
/// TTL keyed by <c>(tenantId, memberId, asOfDate)</c>. Mirrors
/// <see cref="CachingProviderMembershipClient"/> shape and TTL — coverage
/// records can terminate without an explicit signal (mid-year termination,
/// open-enrollment loss), so a longer cache risks stale "no other coverage"
/// results for claims submitted right after a coverage change.
///
/// <para>
/// Cache key is namespaced by resolution path
/// (<c>cached-or-live</c> vs <c>force-refresh</c>) so a force-refresh call
/// doesn't poison the default-path entry — the same convention
/// <see cref="CachingProviderMembershipClient"/> uses. <c>asOfDate</c> is
/// part of the key (rounded to whole days) because the same member can have
/// different coverage at different dates; collapsing the day boundary keeps
/// cache keys finite while preserving service-date semantics within a day.
/// </para>
///
/// <para>
/// Negative results (<c>null</c> from upstream — transport failure) are NOT
/// cached: a transient coverage-service outage shouldn't pin "lookup
/// unavailable" for the full TTL window. The "no COB entries" 404 path
/// produces an empty list (translated by <see cref="HttpCoverageClient"/>);
/// empty IS cached normally because it is a positive answer.
/// </para>
/// </summary>
public sealed class CachingCoverageClient : ICoverageClient
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ICoverageClient _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingCoverageClient(ICoverageClient inner, IMemoryCache cache)
        : this(inner, cache, DefaultTtl) { }

    public CachingCoverageClient(ICoverageClient inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task<IReadOnlyList<CobEntry>?> GetCobEntriesAsync(
        string tenantId,
        string memberId,
        DateTime asOfDate,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, memberId, asOfDate, forceRefresh);

        if (!forceRefresh
            && _cache.TryGetValue<IReadOnlyList<CobEntry>>(key, out var cached)
            && cached is not null)
        {
            return cached;
        }

        var fresh = await _inner
            .GetCobEntriesAsync(tenantId, memberId, asOfDate, forceRefresh, ct)
            .ConfigureAwait(false);

        if (fresh is not null && _ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, _ttl);
        }
        return fresh;
    }

    internal static string BuildCacheKey(
        string tenantId, string memberId, DateTime asOfDate, bool forceRefresh)
    {
        var path = forceRefresh ? "force" : "cached-or-live";
        var dayKey = asOfDate.ToUniversalTime().Date.ToString("yyyyMMdd");
        return $"cob:{path}:{tenantId}:{memberId}:{dayKey}";
    }
}
