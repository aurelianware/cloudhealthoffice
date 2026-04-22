using System.Text.Json;
using CloudHealthOffice.Infrastructure.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// <see cref="ICacheProvider"/> backed by <see cref="IConnectionMultiplexer"/>.
///
/// Values are serialized via <see cref="CloudHealthOfficeJsonOptions.DefaultOptions"/>.
/// Redis tracing from <c>OpenTelemetry.Instrumentation.StackExchangeRedis</c>
/// (A.7.4) attaches at the multiplexer level, so every operation here
/// produces a <c>db.system=redis</c> span without any additional wiring.
///
/// This provider treats keys as opaque strings — tenant prefixing and PHI
/// rejection are applied by <see cref="GuardedCacheProvider"/>, which wraps
/// this type in the production composition in <c>AddChoCaching</c>. The
/// <see cref="CacheScope"/> parameter is intentionally ignored here for
/// the same reason.
///
/// Redis connectivity failures degrade to cache misses (read path) or
/// best-effort no-ops (write/invalidate path) so a Redis incident does not
/// take down caller services — the backing store absorbs the load instead.
/// </summary>
internal sealed class RedisCacheProvider : ICacheProvider
{
    private readonly IConnectionMultiplexer _redis;
    private readonly SingleFlightRunner _singleFlight;
    private readonly ILogger<RedisCacheProvider> _logger;

    public RedisCacheProvider(
        IConnectionMultiplexer redis,
        SingleFlightRunner singleFlight,
        ILogger<RedisCacheProvider> logger)
    {
        _redis        = redis;
        _singleFlight = singleFlight;
        _logger       = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        where T : class
    {
        try
        {
            var db    = _redis.GetDatabase();
            var value = await db.StringGetAsync(key).ConfigureAwait(false);
            if (!value.HasValue) return null;

            try
            {
                return JsonSerializer.Deserialize<T>(value!, CloudHealthOfficeJsonOptions.DefaultOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Corrupted cache entry at {Key} — deleting", LogSanitize(key));
                try { await db.KeyDeleteAsync(key).ConfigureAwait(false); }
                catch (RedisException) { /* best-effort */ }
                return null;
            }
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable on GetAsync({Key}) — treating as miss", LogSanitize(key));
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
    {
        try
        {
            var db      = _redis.GetDatabase();
            var payload = JsonSerializer.Serialize(value, CloudHealthOfficeJsonOptions.DefaultOptions);
            await db.StringSetAsync(key, payload, ttl).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable on SetAsync({Key}) — entry not cached", LogSanitize(key));
        }
    }

    public async Task RemoveAsync(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable on RemoveAsync({Key}) — stale entry will expire via TTL", LogSanitize(key));
        }
    }

    public async Task RemoveAsync(IReadOnlyCollection<string> keys, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
    {
        if (keys.Count == 0) return;
        try
        {
            var db    = _redis.GetDatabase();
            var array = keys.Select(k => (RedisKey)k).ToArray();
            await db.KeyDeleteAsync(array).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis unavailable on bulk RemoveAsync(count={Count})", keys.Count);
        }
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
            var cached = await GetAsync<T>(key, scope, token).ConfigureAwait(false);
            if (cached is not null) return cached;

            var fresh = await factory(token).ConfigureAwait(false);
            if (fresh is not null)
                await SetAsync(key, fresh, ttl, scope, token).ConfigureAwait(false);

            return fresh;
        }, ct);
    }

    /// <summary>
    /// Strips CR/LF/NUL before a cache key enters a log entry. CacheKeyGuard
    /// already rejects those characters at the entry boundary, but CodeQL
    /// (and any other taint tracker) cannot see that invariant across the
    /// Guard → Provider call boundary. Applying defense-in-depth here is
    /// cheap and silences the cs/log-forging alerts.
    /// </summary>
    private static string LogSanitize(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        return key
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Replace("\0", string.Empty);
    }
}
