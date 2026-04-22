using Microsoft.Extensions.Caching.Memory;

namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// <see cref="ICacheProvider"/> backed by <see cref="IMemoryCache"/> — used in
/// Development and in unit tests. Values are stored by reference (no
/// serialization round-trip), so this provider is noticeably faster than
/// Redis in tight test loops but, unlike Redis, will silently tolerate type
/// mismatches between writer and reader when <typeparamref name="T"/> differs
/// across calls on the same key. Production code should always use a
/// consistent type per key; the Redis implementation would raise a
/// JsonException on the mismatch.
///
/// The <see cref="CacheScope"/> parameter is ignored here — scope-based
/// prefixing happens in <see cref="GuardedCacheProvider"/>, which wraps
/// this class in the production composition.
/// </summary>
internal sealed class InMemoryCacheProvider : ICacheProvider
{
    private readonly IMemoryCache _cache;
    private readonly SingleFlightRunner _singleFlight;

    public InMemoryCacheProvider(IMemoryCache cache, SingleFlightRunner singleFlight)
    {
        _cache        = cache;
        _singleFlight = singleFlight;
    }

    public Task<T?> GetAsync<T>(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        where T : class
    {
        return Task.FromResult(_cache.TryGetValue<T>(key, out var value) ? value : null);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
    {
        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl
        });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IReadOnlyCollection<string> keys, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
    {
        foreach (var k in keys) _cache.Remove(k);
        return Task.CompletedTask;
    }

    public Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan ttl,
        CacheScope scope = CacheScope.Tenant,
        CancellationToken ct = default)
        where T : class
    {
        return _singleFlight.RunAsync<T>(key, async token =>
        {
            if (_cache.TryGetValue<T>(key, out var cached) && cached is not null)
                return cached;

            var fresh = await factory(token).ConfigureAwait(false);
            if (fresh is not null) await SetAsync(key, fresh, ttl, scope, token).ConfigureAwait(false);
            return fresh;
        }, ct);
    }
}
