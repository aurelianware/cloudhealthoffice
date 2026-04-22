namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Decorator that routes every <see cref="ICacheProvider"/> call through
/// <see cref="CacheKeyGuard"/> before delegating to the inner provider.
/// This is the type actually registered as <see cref="ICacheProvider"/>
/// by <c>AddChoCaching</c>; consumers never see the raw Redis/InMemory/Null
/// implementation and therefore cannot sidestep tenant prefixing or PHI
/// rejection.
/// </summary>
internal sealed class GuardedCacheProvider : ICacheProvider
{
    private readonly ICacheProvider _inner;
    private readonly CacheKeyGuard _guard;

    public GuardedCacheProvider(ICacheProvider inner, CacheKeyGuard guard)
    {
        _inner = inner;
        _guard = guard;
    }

    public Task<T?> GetAsync<T>(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        where T : class
        => _inner.GetAsync<T>(_guard.Build(key, scope), scope, ct);

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        => _inner.SetAsync(_guard.Build(key, scope), value, ttl, scope, ct);

    public Task RemoveAsync(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        => _inner.RemoveAsync(_guard.Build(key, scope), scope, ct);

    public Task RemoveAsync(IReadOnlyCollection<string> keys, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        => _inner.RemoveAsync(_guard.BuildMany(keys, scope), scope, ct);

    public Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan ttl,
        CacheScope scope = CacheScope.Tenant,
        CancellationToken ct = default)
        where T : class
        => _inner.GetOrSetAsync(_guard.Build(key, scope), factory, ttl, scope, ct);
}
