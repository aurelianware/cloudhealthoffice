using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Coalesces concurrent cache-miss callers on the same key so the read-through
/// factory is invoked at most once per miss. The alternative is a cold-start
/// stampede where N concurrent requests each hit the backing store.
///
/// Implementation: a ConcurrentDictionary of refcounted entries. Each entry
/// owns a <see cref="SemaphoreSlim"/>(1,1); the first waiter runs the factory
/// while the rest block, then the entry is removed when its refcount returns
/// to zero. Under normal load the dictionary oscillates around
/// <em>concurrent outstanding misses</em>, NOT total distinct keys — no
/// unbounded growth.
///
/// The <paramref name="maxInFlight"/> cap is a safety ceiling for pathological
/// cases (a key space expanding faster than factories complete). When the cap
/// is exceeded, an opportunistic sweep removes any entry currently at
/// refcount zero and bumps <c>cho_cache_singleflight_evictions</c>. An
/// evicted key simply loses its coalescing guarantee for that one miss
/// window — duplicate factory invocations, not correctness loss.
/// </summary>
internal sealed class SingleFlightRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly int _maxInFlight;
    private readonly Counter<long>? _evictions;

    public SingleFlightRunner(int maxInFlight, Meter? meter = null)
    {
        _maxInFlight = maxInFlight > 0 ? maxInFlight : 10_000;
        _evictions   = meter?.CreateCounter<long>(
            "cho_cache_singleflight_evictions",
            description: "Coalescer entries pruned because in-flight cap was exceeded.");
    }

    public async Task<T?> RunAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken ct)
        where T : class
    {
        // GetOrAdd may invoke the factory redundantly under contention; the
        // losing Entry + its semaphore are GC'd without ever entering the
        // dict. Every caller increments the surviving entry's refcount
        // identically — no "was I the creator?" branch needed.
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        Interlocked.Increment(ref entry.RefCount);

        if (_entries.Count > _maxInFlight) TryEvict();

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await factory(ct).ConfigureAwait(false);
            }
            finally
            {
                entry.Semaphore.Release();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref entry.RefCount) == 0)
            {
                if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
                    entry.Semaphore.Dispose();
            }
        }
    }

    private void TryEvict()
    {
        var pruned = 0;
        foreach (var kv in _entries)
        {
            if (Volatile.Read(ref kv.Value.RefCount) == 0 &&
                _entries.TryRemove(kv))
            {
                kv.Value.Semaphore.Dispose();
                pruned++;
                if (_entries.Count <= _maxInFlight) break;
            }
        }
        if (pruned > 0) _evictions?.Add(pruned);
    }

    public void Dispose()
    {
        foreach (var kv in _entries)
        {
            if (_entries.TryRemove(kv))
                kv.Value.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }
}
