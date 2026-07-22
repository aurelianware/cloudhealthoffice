using System.Collections.Concurrent;
using CloudHealthOffice.NcciEngine.Domain;

namespace CloudHealthOffice.NcciEngine.Services;

internal sealed class NcciLookupCache
{
    // NCCI/MUE reference data is quarterly (CMS cadence); explicit updates go through
    // ImportQuarterlyUpdateAsync -> InvalidateTenant, which bypasses this TTL entirely.
    // The TTL only bounds staleness for edits made outside that path, so it can be long
    // without risking masking a real update -- a short TTL just forces avoidable re-lookups
    // on every distinct (code-pair, service-date) combination within a single long run.
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<PairCacheKey, CacheEntry<NcciEditPair>> _pairs = new();
    private readonly ConcurrentDictionary<MueCacheKey, CacheEntry<MueEntry>> _mues = new();
    private long _nextSweepTicks = DateTimeOffset.UtcNow.Add(DefaultTtl).UtcTicks;

    public Task<NcciEditPair?> GetEditPairAsync(
        string tenantId,
        string column1Code,
        string column2Code,
        DateOnly serviceDate,
        Func<CancellationToken, Task<NcciEditPair?>> factory,
        CancellationToken ct)
    {
        var key = new PairCacheKey(
            tenantId,
            NormalizeCode(column1Code),
            NormalizeCode(column2Code),
            serviceDate);

        return GetOrCreateAsync(_pairs, key, factory, ct);
    }

    public Task<MueEntry?> GetMueEntryAsync(
        string tenantId,
        string procedureCode,
        DateOnly serviceDate,
        Func<CancellationToken, Task<MueEntry?>> factory,
        CancellationToken ct)
    {
        var key = new MueCacheKey(tenantId, NormalizeCode(procedureCode), serviceDate);
        return GetOrCreateAsync(_mues, key, factory, ct);
    }

    public void InvalidateTenant(string tenantId)
    {
        foreach (var key in _pairs.Keys.Where(k => string.Equals(k.TenantId, tenantId, StringComparison.Ordinal)))
        {
            _pairs.TryRemove(key, out _);
        }

        foreach (var key in _mues.Keys.Where(k => string.Equals(k.TenantId, tenantId, StringComparison.Ordinal)))
        {
            _mues.TryRemove(key, out _);
        }
    }

    private async Task<T?> GetOrCreateAsync<TKey, T>(
        ConcurrentDictionary<TKey, CacheEntry<T>> cache,
        TKey key,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken ct)
        where TKey : notnull
    {
        MaybeSweep();

        var now = DateTimeOffset.UtcNow;
        var entry = cache.GetOrAdd(key, _ => NewEntry(factory, now));

        if (entry.ExpiresAt <= now)
        {
            var replacement = NewEntry(factory, now);
            entry = cache.AddOrUpdate(key, replacement, (_, current) =>
                current.ExpiresAt <= now ? replacement : current);
        }

        try
        {
            return await entry.Value.Value.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Per-caller cancellation; the shared task continues — do not evict.
            throw;
        }
        catch
        {
            cache.TryRemove(key, out _);
            throw;
        }
    }

    private void MaybeSweep()
    {
        var now = DateTimeOffset.UtcNow;
        var next = Volatile.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next) return;
        if (Interlocked.CompareExchange(ref _nextSweepTicks, now.Add(DefaultTtl).UtcTicks, next) != next)
            return;

        SweepExpired(_pairs, now);
        SweepExpired(_mues, now);
    }

    private static void SweepExpired<TKey, T>(
        ConcurrentDictionary<TKey, CacheEntry<T>> cache,
        DateTimeOffset now)
        where TKey : notnull
    {
        foreach (var key in cache.Keys.ToList())
        {
            if (cache.TryGetValue(key, out var entry) && entry.ExpiresAt <= now)
                cache.TryRemove(key, out _);
        }
    }

    private static CacheEntry<T> NewEntry<T>(
        Func<CancellationToken, Task<T?>> factory,
        DateTimeOffset now)
    {
        return new CacheEntry<T>(
            new Lazy<Task<T?>>(() => factory(CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication),
            now.Add(DefaultTtl));
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private sealed record CacheEntry<T>(Lazy<Task<T?>> Value, DateTimeOffset ExpiresAt);

    private sealed record PairCacheKey(
        string TenantId,
        string Column1Code,
        string Column2Code,
        DateOnly ServiceDate);

    private sealed record MueCacheKey(
        string TenantId,
        string ProcedureCode,
        DateOnly ServiceDate);
}
