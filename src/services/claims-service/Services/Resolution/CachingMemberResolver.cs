using Microsoft.Extensions.Caching.Memory;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Decorator over <see cref="IMemberResolver"/> with the same 5-minute
/// in-process TTL as <see cref="CachingBenefitPlanResolver"/>, keyed by
/// <c>(tenantId, memberId)</c>. Negative results are not cached so a
/// transient member-service outage doesn't pin "missing" for the full
/// TTL window.
/// </summary>
public sealed class CachingMemberResolver : IMemberResolver
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly IMemberResolver _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingMemberResolver(IMemberResolver inner, IMemoryCache cache)
        : this(inner, cache, DefaultTtl) { }

    public CachingMemberResolver(IMemberResolver inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task<ResolvedMember?> GetMemberAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, memberId);
        if (_cache.TryGetValue<ResolvedMember>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var fresh = await _inner.GetMemberAsync(tenantId, memberId, ct).ConfigureAwait(false);
        if (fresh is not null && _ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, _ttl);
        }
        return fresh;
    }

    internal static string BuildCacheKey(string tenantId, string memberId) =>
        $"member:{tenantId}:{memberId}";
}
