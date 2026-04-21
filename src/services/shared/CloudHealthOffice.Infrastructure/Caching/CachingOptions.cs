namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Binds from the <c>Caching</c> configuration section. Shape mirrors
/// <c>MessagingOptions</c> — Auto / Redis / InMemory / Null — so the two
/// shared abstractions resolve identically.
/// </summary>
public class CachingOptions
{
    public const string SectionName = "Caching";

    /// <summary>
    /// Backend selection:
    ///   <c>Auto</c>     — Redis when <see cref="RedisConnectionString"/> is set
    ///                     AND environment is not Development, else InMemory.
    ///   <c>Redis</c>    — force Redis; throws at startup if the connection
    ///                     string is missing.
    ///   <c>InMemory</c> — force in-process <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>.
    ///   <c>Null</c>     — no-op, for explicit-disable scenarios (canary
    ///                     rollbacks, tests that want every request to hit
    ///                     the backing store).
    /// </summary>
    public string Backend { get; set; } = "Auto";

    /// <summary>StackExchange.Redis connection string.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Upper bound on the number of in-flight cache-miss coalescer entries.
    /// When this is exceeded the coalescer prunes released entries
    /// opportunistically and bumps <c>cho_cache_singleflight_evictions</c>.
    /// Default 10000 — well above expected cold-start fan-in; large enough
    /// that legitimate load never trips it, small enough that a runaway
    /// high-cardinality key space cannot OOM a pod.
    /// </summary>
    public int SingleFlightMaxInFlight { get; set; } = 10_000;
}
