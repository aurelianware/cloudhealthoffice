using Microsoft.Extensions.Caching.Memory;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Decorator over <see cref="ICoverageResolver"/> with the same 5-minute TTL
/// and day-collapsed cache key as <see cref="CachingCoverageClient"/> —
/// coverage records can terminate without an explicit signal, so a longer
/// cache risks resolving a stale plan for claims submitted right after a
/// coverage change. Negative results (no active coverage, or transport
/// failure) are not cached, matching <see cref="CachingMemberResolver"/>.
/// </summary>
public sealed class CachingCoverageResolver : ICoverageResolver
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ICoverageResolver _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingCoverageResolver(ICoverageResolver inner, IMemoryCache cache)
        : this(inner, cache, DefaultTtl) { }

    public CachingCoverageResolver(ICoverageResolver inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task<string?> ResolveBenefitPlanIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null,
        CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, memberId, serviceDate, insuranceLineCode);
        if (_cache.TryGetValue<string>(key, out var cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        var fresh = await _inner
            .ResolveBenefitPlanIdAsync(tenantId, memberId, serviceDate, insuranceLineCode, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(fresh) && _ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, _ttl);
        }
        return fresh;
    }

    internal static string BuildCacheKey(
        string tenantId, string memberId, DateTime serviceDate, string? insuranceLineCode)
    {
        var dayKey = serviceDate.ToUniversalTime().Date.ToString("yyyyMMdd");
        var line = string.IsNullOrWhiteSpace(insuranceLineCode) ? "any" : insuranceLineCode;
        return $"coverage-plan:{tenantId}:{memberId}:{dayKey}:{line}";
    }
}
