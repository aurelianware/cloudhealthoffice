namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Shared cache abstraction for Cloud Health Office services.
///
/// Scoped deliberately to string key / object value with TTL — the 90% shape
/// that ProviderEnrollmentService and PriorAuthRuleEngine already use against
/// Redis. Two callers are deliberate exceptions and keep their
/// <c>IConnectionMultiplexer</c> dependency:
///   • <c>RedisAccumulatorService</c> — needs server-side atomic
///     <c>HINCRBYFLOAT</c> on Redis hashes to avoid read-modify-write races
///     between concurrent claim adjudications.
///   • <c>RedisPaRuleRepository</c>   — needs <c>SCAN</c>-based pattern delete
///     on state-level invalidation; the post-delete cache keys cannot be
///     reconstructed from a <c>ruleId</c> alone.
/// Both classes carry a <c>&lt;remarks&gt;</c> block explaining why. New
/// consumers that look like caches should use this interface; new consumers
/// that need Redis-native semantics (hashes, counters, pub/sub, locks,
/// pattern deletion) own their own multiplexer and document the reason.
///
/// All operations route through <c>CacheKeyGuard</c> in the production
/// composition — keys are tenant-prefixed and screened for PHI tokens
/// before they reach the backend. The <paramref name="scope"/> parameter
/// defaults to <see cref="CacheScope.Tenant"/>; pass
/// <see cref="CacheScope.Global"/> deliberately for platform-wide entries.
///
/// See <c>docs/architecture/shared-cache.md</c> for the decision tree.
/// </summary>
public interface ICacheProvider
{
    /// <summary>
    /// Read a cached value. Returns <c>null</c> if the key is absent.
    /// Deserialization failures are treated as misses — the corrupted entry
    /// is best-effort deleted and <c>null</c> returned.
    /// </summary>
    Task<T?> GetAsync<T>(string key,
                         CacheScope scope = CacheScope.Tenant,
                         CancellationToken ct = default)
        where T : class;

    /// <summary>
    /// Write a value with an explicit TTL. Overwrites any existing entry at
    /// the same key.
    /// </summary>
    Task SetAsync<T>(string key, T value, TimeSpan ttl,
                     CacheScope scope = CacheScope.Tenant,
                     CancellationToken ct = default);

    /// <summary>Delete by key. No-op if the key does not exist.</summary>
    Task RemoveAsync(string key,
                     CacheScope scope = CacheScope.Tenant,
                     CancellationToken ct = default);

    /// <summary>
    /// Delete many keys in one round trip. Used by
    /// <c>RedisPaRuleRepository.BulkUpsertAsync</c> to invalidate every
    /// distinct cache key touched by a bulk write.
    /// </summary>
    Task RemoveAsync(IReadOnlyCollection<string> keys,
                     CacheScope scope = CacheScope.Tenant,
                     CancellationToken ct = default);

    /// <summary>
    /// Read-through helper. If the key is absent, invoke <paramref name="factory"/>,
    /// cache the result with <paramref name="ttl"/>, and return it.
    /// Concurrent misses for the same key on the same process are coalesced:
    /// the factory is invoked at most once even under a stampede. See the
    /// per-key <c>SingleFlightRunner</c> for the memory-bounded implementation.
    ///
    /// The factory MAY return <c>null</c>; a null result is NOT cached — the
    /// next caller will re-invoke. (This matches the "tenant not configured"
    /// semantics in <c>RedisTenantEnrollmentConfigRepository</c>.)
    /// </summary>
    Task<T?> GetOrSetAsync<T>(string key,
                              Func<CancellationToken, Task<T?>> factory,
                              TimeSpan ttl,
                              CacheScope scope = CacheScope.Tenant,
                              CancellationToken ct = default)
        where T : class;
}
