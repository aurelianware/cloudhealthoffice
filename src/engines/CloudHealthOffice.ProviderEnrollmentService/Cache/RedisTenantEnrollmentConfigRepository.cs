using System.Text.Json;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Redis caching decorator for ITenantEnrollmentConfigRepository.
///
/// Wraps either TenantEnrollmentConfigRepositoryCosmos or
/// TenantEnrollmentConfigRepositoryMongo and adds a Redis read-through
/// cache in front of every GetAsync call.
///
/// Follows the exact CHO Redis pattern from RedisAccumulatorService:
///   IConnectionMultiplexer → IDatabase → StringGetAsync / StringSetAsync
///
/// ── Key layout ────────────────────────────────────────────────────
///
///   enrollment:config:{tenantId}
///
///   Example: enrollment:config:pchp
///
/// ── Cache behaviour ───────────────────────────────────────────────
///
///   GetAsync:    Redis hit  → deserialize and return
///                Redis miss → call inner repo → cache result → return
///   UpsertAsync: write to inner repo → delete Redis key (invalidate)
///   DeleteAsync: delete from inner repo → delete Redis key
///   ListAsync:   always hits inner repo (admin path — not cached)
///
/// ── TTL ───────────────────────────────────────────────────────────
///
///   Default 5 minutes (ProviderEnrollmentOptions.TenantConfigCacheTtl).
///   Short because config changes (gate mode flip from Warn → Enforce)
///   must propagate to all pods within one cache window.
///   Operators can lower this to 1 minute during rollout.
///
/// ── Null tenants ──────────────────────────────────────────────────
///
///   A null result from the inner repo (tenant not configured) is NOT
///   cached. This lets a new tenant config document become visible
///   immediately after seeding without a forced cache flush.
///
/// ── Redis unavailability ──────────────────────────────────────────
///
///   Any Redis exception is caught and logged as a warning; the call
///   falls through to the inner repository. The gate continues to
///   function — just with database latency on every call rather than
///   sub-millisecond Redis reads.
/// </summary>
public sealed class RedisTenantEnrollmentConfigRepository : ITenantEnrollmentConfigRepository
{
    private readonly ITenantEnrollmentConfigRepository _inner;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisTenantEnrollmentConfigRepository> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public RedisTenantEnrollmentConfigRepository(
        ITenantEnrollmentConfigRepository inner,
        IConnectionMultiplexer redis,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<RedisTenantEnrollmentConfigRepository> logger)
    {
        _inner  = inner;
        _redis  = redis;
        _ttl    = options.Value.TenantConfigCacheTtl;
        _logger = logger;
    }

    // ── Read-through ──────────────────────────────────────────────

    public async Task<TenantEnrollmentConfig?> GetAsync(
        string tenantId, CancellationToken ct = default)
    {
        var key = MakeKey(tenantId);

        // 1. Try Redis
        try
        {
            var db    = _redis.GetDatabase();
            var value = await db.StringGetAsync(key);

            if (value.HasValue)
            {
                _logger.LogDebug("TenantEnrollmentConfig Redis hit for tenant {TenantId}", SanitizeForLog(tenantId));
                try
                {
                    var cached = JsonSerializer.Deserialize<TenantEnrollmentConfig>(value!, _json);
                    if (cached is not null)
                        return cached;

                    // Null deserialization (corrupted entry) — treat as cache miss
                    _logger.LogWarning(
                        "TenantEnrollmentConfig Redis entry for tenant {TenantId} deserialized to null — deleting corrupted key",
                        SanitizeForLog(tenantId));
                    await db.KeyDeleteAsync(key);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "TenantEnrollmentConfig Redis entry for tenant {TenantId} failed to deserialize — deleting corrupted key",
                        SanitizeForLog(tenantId));
                    await db.KeyDeleteAsync(key);
                }
            }
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable for TenantEnrollmentConfig read — falling through to store");
        }

        // 2. Miss — hit the backing store
        var config = await _inner.GetAsync(tenantId, ct);

        // 3. Cache non-null results only (null = tenant not configured — don't cache)
        if (config is not null)
        {
            await TryCacheAsync(key, config);
        }

        return config;
    }

    // ── Write-through invalidation ────────────────────────────────

    public async Task UpsertAsync(
        TenantEnrollmentConfig config, CancellationToken ct = default)
    {
        // Write to backing store first — Redis is a cache, not the source of truth
        await _inner.UpsertAsync(config, ct);
        await TryInvalidateAsync(MakeKey(config.TenantId));
    }

    public async Task DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        await _inner.DeleteAsync(tenantId, ct);
        await TryInvalidateAsync(MakeKey(tenantId));
    }

    // ── Admin list — always hits store ───────────────────────────

    public Task<IReadOnlyList<TenantEnrollmentConfig>> ListAsync(
        CancellationToken ct = default) => _inner.ListAsync(ct);

    // ── Helpers ───────────────────────────────────────────────────

    private static RedisKey MakeKey(string tenantId) =>
        $"enrollment:config:{tenantId}";

    private async Task TryCacheAsync(RedisKey key, TenantEnrollmentConfig config)
    {
        try
        {
            var db      = _redis.GetDatabase();
            var payload = JsonSerializer.Serialize(config, _json);
            await db.StringSetAsync(key, payload, _ttl);

            _logger.LogDebug(
                "TenantEnrollmentConfig cached for tenant {TenantId} with TTL {Ttl}",
                config.TenantId, _ttl);
        }
        catch (RedisException ex)
        {
            // Non-fatal — next request will hit the backing store again
            _logger.LogWarning(ex,
                "Failed to cache TenantEnrollmentConfig for tenant {TenantId}",
                config.TenantId);
        }
    }

    private async Task TryInvalidateAsync(RedisKey key)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
            _logger.LogDebug("TenantEnrollmentConfig cache invalidated for key {Key}", (string)key);
        }
        catch (RedisException ex)
        {
            // Non-fatal — stale entry will expire within TTL window
            _logger.LogWarning(ex,
                "Failed to invalidate TenantEnrollmentConfig cache for key {Key}", (string)key);
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
