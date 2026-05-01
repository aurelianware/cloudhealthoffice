using Microsoft.Extensions.Caching.Memory;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Decorator over <see cref="ICredentialingStatusClient"/> with a 1-hour
/// in-process TTL keyed by
/// <c>(tenantId, providerId, asOfDate)</c>. Mirrors
/// <see cref="CachingProviderMembershipClient"/> shape but with a longer
/// TTL — credentialing transitions are explicit, audit-trailed events
/// (provider-service capability 5.6 event chain) and within an hour the
/// staleness window is operationally acceptable. Membership rows can
/// terminate without an explicit event signal; credentialing cannot.
///
/// <para>
/// Negative results (<c>null</c> from upstream) are NOT cached, matching
/// <see cref="CachingProviderMembershipClient"/>.
/// </para>
/// </summary>
public sealed class CachingCredentialingStatusClient : ICredentialingStatusClient
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    private readonly ICredentialingStatusClient _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachingCredentialingStatusClient(ICredentialingStatusClient inner, IMemoryCache cache)
        : this(inner, cache, DefaultTtl) { }

    public CachingCredentialingStatusClient(ICredentialingStatusClient inner, IMemoryCache cache, TimeSpan ttl)
    {
        _inner = inner;
        _cache = cache;
        _ttl = ttl;
    }

    public async Task<CredentialingStatusSnapshot?> GetStatusAsOfAsync(
        string tenantId,
        string providerId,
        DateTime asOfDate,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, providerId, asOfDate, forceRefresh);

        if (!forceRefresh
            && _cache.TryGetValue<CredentialingStatusSnapshot>(key, out var cached)
            && cached is not null)
        {
            return cached;
        }

        var fresh = await _inner
            .GetStatusAsOfAsync(tenantId, providerId, asOfDate, forceRefresh, ct)
            .ConfigureAwait(false);

        if (fresh is not null && _ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, _ttl);
        }
        return fresh;
    }

    internal static string BuildCacheKey(
        string tenantId, string providerId, DateTime asOfDate, bool forceRefresh)
    {
        var path = forceRefresh ? "force" : "cached-or-live";
        var dayKey = asOfDate.ToUniversalTime().Date.ToString("yyyyMMdd");
        return $"credentialing:{path}:{tenantId}:{providerId}:{dayKey}";
    }
}
