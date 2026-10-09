using System.Collections.Concurrent;
using CloudHealthOffice.NcciEngine.Domain;

namespace CloudHealthOffice.NcciEngine.Services;

/// <summary>
/// What a lookup is scoped to: the NCCI setting, whether setting-less seed
/// rows still apply, and the table <see cref="Models.NcciTableVersion.LoadStamp"/>
/// the answer was read under.
/// </summary>
internal sealed record NcciLookupScope(string? Setting, bool IncludeUnscoped, string? Stamp);

internal sealed class NcciLookupCache
{
    // NCCI/MUE reference data is quarterly (CMS cadence). Every CMS load writes a new
    // NcciTableVersion.LoadStamp, and lookup entries are keyed by the stamp they were
    // read under, so a load done by any process (another replica, benefit-plan-service
    // vs claims-service) retires this process's entries as soon as it re-reads the
    // version -- at most VersionTtl later. InvalidateTenant clears the local process
    // immediately. The long TTL only bounds staleness for edits made outside both paths.
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    /// <summary>How long a process trusts its copy of a tenant's table version.</summary>
    internal static readonly TimeSpan VersionTtl = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _versionTtl;

    public NcciLookupCache() : this(VersionTtl)
    {
    }

    internal NcciLookupCache(TimeSpan versionTtl)
    {
        _versionTtl = versionTtl;
    }

    private readonly ConcurrentDictionary<PairCacheKey, CacheEntry<NcciEditPair>> _pairs = new();
    private readonly ConcurrentDictionary<MueCacheKey, CacheEntry<MueEntry>> _mues = new();
    private readonly ConcurrentDictionary<string, CacheEntry<Models.NcciTableVersion>> _versions = new(StringComparer.Ordinal);
    private long _nextSweepTicks = DateTimeOffset.UtcNow.Add(DefaultTtl).UtcTicks;

    public Task<Models.NcciTableVersion?> GetVersionAsync(
        string tenantId,
        Func<CancellationToken, Task<Models.NcciTableVersion?>> factory,
        CancellationToken ct)
        => GetOrCreateAsync(_versions, tenantId, factory, ct, _versionTtl);

    public Task<NcciEditPair?> GetEditPairAsync(
        string tenantId,
        string column1Code,
        string column2Code,
        DateOnly serviceDate,
        NcciLookupScope scope,
        Func<CancellationToken, Task<NcciEditPair?>> factory,
        CancellationToken ct)
    {
        var key = new PairCacheKey(
            tenantId,
            NormalizeCode(column1Code),
            NormalizeCode(column2Code),
            serviceDate,
            scope);

        return GetOrCreateAsync(_pairs, key, factory, ct);
    }

    public Task<MueEntry?> GetMueEntryAsync(
        string tenantId,
        string procedureCode,
        DateOnly serviceDate,
        NcciLookupScope scope,
        Func<CancellationToken, Task<MueEntry?>> factory,
        CancellationToken ct)
    {
        var key = new MueCacheKey(tenantId, NormalizeCode(procedureCode), serviceDate, scope);
        return GetOrCreateAsync(_mues, key, factory, ct);
    }

    public void InvalidateTenant(string tenantId)
    {
        _versions.TryRemove(tenantId, out _);

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
        CancellationToken ct,
        TimeSpan? ttl = null)
        where TKey : notnull
    {
        MaybeSweep();

        var now = DateTimeOffset.UtcNow;
        var lifetime = ttl ?? DefaultTtl;
        var entry = cache.GetOrAdd(key, _ => NewEntry(factory, now, lifetime));

        if (entry.ExpiresAt <= now)
        {
            var replacement = NewEntry(factory, now, lifetime);
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
        SweepExpired(_versions, now);
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
        DateTimeOffset now,
        TimeSpan ttl)
    {
        return new CacheEntry<T>(
            new Lazy<Task<T?>>(() => factory(CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication),
            now.Add(ttl));
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private sealed record CacheEntry<T>(Lazy<Task<T?>> Value, DateTimeOffset ExpiresAt);

    private sealed record PairCacheKey(
        string TenantId,
        string Column1Code,
        string Column2Code,
        DateOnly ServiceDate,
        NcciLookupScope Scope);

    private sealed record MueCacheKey(
        string TenantId,
        string ProcedureCode,
        DateOnly ServiceDate,
        NcciLookupScope Scope);
}
