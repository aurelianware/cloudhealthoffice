using System.Collections.Concurrent;
using CloudHealthOffice.NcciEngine.Domain;

namespace CloudHealthOffice.NcciEngine.Services;

internal sealed class NcciLookupCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<PairCacheKey, CacheEntry<NcciEditPair>> _pairs = new();
    private readonly ConcurrentDictionary<MueCacheKey, CacheEntry<MueEntry>> _mues = new();

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

    private static async Task<T?> GetOrCreateAsync<TKey, T>(
        ConcurrentDictionary<TKey, CacheEntry<T>> cache,
        TKey key,
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken ct)
        where TKey : notnull
    {
        var now = DateTimeOffset.UtcNow;
        var entry = cache.GetOrAdd(key, _ => NewEntry(factory, ct, now));

        if (entry.ExpiresAt <= now)
        {
            var replacement = NewEntry(factory, ct, now);
            entry = cache.AddOrUpdate(key, replacement, (_, current) =>
                current.ExpiresAt <= now ? replacement : current);
        }

        try
        {
            return await entry.Value.Value;
        }
        catch
        {
            cache.TryRemove(key, out _);
            throw;
        }
    }

    private static CacheEntry<T> NewEntry<T>(
        Func<CancellationToken, Task<T?>> factory,
        CancellationToken ct,
        DateTimeOffset now)
    {
        return new CacheEntry<T>(
            new Lazy<Task<T?>>(() => factory(ct), LazyThreadSafetyMode.ExecutionAndPublication),
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
