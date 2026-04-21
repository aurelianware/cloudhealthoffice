namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// Explicit-disable no-op <see cref="ICacheProvider"/>. <see cref="GetAsync"/>
/// always returns <c>null</c>; writes and invalidations are silently dropped;
/// <see cref="GetOrSetAsync"/> invokes the factory on every call without
/// caching. Useful for canary rollbacks (force every request to hit the
/// backing store) and for tests that want to exercise the cold path.
/// </summary>
internal sealed class NullCacheProvider : ICacheProvider
{
    public Task<T?> GetAsync<T>(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        where T : class
        => Task.FromResult<T?>(null);

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveAsync(string key, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveAsync(IReadOnlyCollection<string> keys, CacheScope scope = CacheScope.Tenant, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<T?> GetOrSetAsync<T>(
        string key,
        Func<CancellationToken, Task<T?>> factory,
        TimeSpan ttl,
        CacheScope scope = CacheScope.Tenant,
        CancellationToken ct = default)
        where T : class
        => factory(ct);
}
