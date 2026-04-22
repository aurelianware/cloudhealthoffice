using CloudHealthOffice.Infrastructure.Caching;

namespace CloudHealthOffice.Infrastructure.Tests.Caching;

/// <summary>
/// Shared contract every <see cref="ICacheProvider"/> implementation must pass.
/// Mirrors the <c>MessageBusContractTests</c> pattern: the InMemory fixture
/// runs in every CI build and the real-Redis fixture
/// (<see cref="RedisCacheProviderContractTests"/>) runs only when an env
/// var is set, so "in-memory works, Redis is subtly different" cannot
/// silently escape notice.
///
/// These tests operate BELOW the guard — the SUT is the raw implementation,
/// not <c>GuardedCacheProvider</c>, so keys are opaque strings without
/// tenant prefix.
/// </summary>
public abstract class CacheProviderContractTests : IAsyncLifetime
{
    protected ICacheProvider Cache { get; private set; } = default!;
    private IDisposable? _owned;

    protected abstract ValueTask<(ICacheProvider, IDisposable?)> CreateCacheAsync();

    protected virtual string KeyFor(string test) => $"contract-{test}-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        (Cache, _owned) = await CreateCacheAsync();
    }

    public Task DisposeAsync()
    {
        _owned?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task GetAsync_Missing_ReturnsNull()
    {
        var result = await Cache.GetAsync<Payload>(KeyFor("miss"));
        Assert.Null(result);
    }

    [SkippableFact]
    public async Task SetThenGet_RoundTripsValue()
    {
        var key = KeyFor("roundtrip");
        await Cache.SetAsync(key, new Payload("hello", 42), TimeSpan.FromMinutes(1));

        var result = await Cache.GetAsync<Payload>(key);
        Assert.NotNull(result);
        Assert.Equal("hello", result!.Name);
        Assert.Equal(42, result.Value);
    }

    [SkippableFact]
    public async Task SetWithTtl_ExpiresAfterTtl()
    {
        var key = KeyFor("ttl");
        await Cache.SetAsync(key, new Payload("expires", 1), TimeSpan.FromMilliseconds(150));

        // Immediately: hit
        Assert.NotNull(await Cache.GetAsync<Payload>(key));

        await Task.Delay(300);
        // After TTL: miss
        Assert.Null(await Cache.GetAsync<Payload>(key));
    }

    [SkippableFact]
    public async Task RemoveAsync_DeletesKey()
    {
        var key = KeyFor("remove");
        await Cache.SetAsync(key, new Payload("rm", 1), TimeSpan.FromMinutes(1));
        Assert.NotNull(await Cache.GetAsync<Payload>(key));

        await Cache.RemoveAsync(key);
        Assert.Null(await Cache.GetAsync<Payload>(key));
    }

    [SkippableFact]
    public async Task RemoveAsync_Bulk_DeletesAllKeys()
    {
        var keys = Enumerable.Range(0, 5).Select(i => KeyFor($"bulk-{i}")).ToArray();
        foreach (var k in keys)
            await Cache.SetAsync(k, new Payload(k, 1), TimeSpan.FromMinutes(1));

        await Cache.RemoveAsync(keys);

        foreach (var k in keys)
            Assert.Null(await Cache.GetAsync<Payload>(k));
    }

    [SkippableFact]
    public async Task RemoveAsync_Bulk_EmptyCollection_IsNoOp()
    {
        await Cache.RemoveAsync(Array.Empty<string>()); // must not throw
    }

    [SkippableFact]
    public async Task GetOrSetAsync_Miss_InvokesFactory_CachesResult()
    {
        var key = KeyFor("gos-miss");
        var invocations = 0;

        var first = await Cache.GetOrSetAsync<Payload>(
            key,
            _ => { Interlocked.Increment(ref invocations); return Task.FromResult<Payload?>(new Payload("fresh", 1)); },
            TimeSpan.FromMinutes(1));

        Assert.NotNull(first);
        Assert.Equal(1, invocations);

        // Second call: hit (factory not invoked)
        var second = await Cache.GetOrSetAsync<Payload>(
            key,
            _ => { Interlocked.Increment(ref invocations); return Task.FromResult<Payload?>(new Payload("stale", 2)); },
            TimeSpan.FromMinutes(1));

        Assert.Equal("fresh", second!.Name);
        Assert.Equal(1, invocations);
    }

    [SkippableFact]
    public async Task GetOrSetAsync_NullResult_IsNotCached()
    {
        var key = KeyFor("gos-null");
        var invocations = 0;

        var first = await Cache.GetOrSetAsync<Payload>(
            key,
            _ => { Interlocked.Increment(ref invocations); return Task.FromResult<Payload?>(null); },
            TimeSpan.FromMinutes(1));

        Assert.Null(first);
        Assert.Equal(1, invocations);

        // Next call: factory invoked again (null not cached)
        var second = await Cache.GetOrSetAsync<Payload>(
            key,
            _ => { Interlocked.Increment(ref invocations); return Task.FromResult<Payload?>(new Payload("now", 1)); },
            TimeSpan.FromMinutes(1));

        Assert.Equal("now", second!.Name);
        Assert.Equal(2, invocations);
    }

    [SkippableFact]
    public async Task GetOrSetAsync_ConcurrentMisses_FactoryInvokedOnce()
    {
        var key = KeyFor("gos-coalesce");
        var invocations = 0;
        var gate = new TaskCompletionSource();

        // 20 concurrent cold-start callers race against the same key.
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            return await Cache.GetOrSetAsync<Payload>(
                key,
                async _ =>
                {
                    Interlocked.Increment(ref invocations);
                    // Hold inside the factory long enough that every other
                    // task has queued on the semaphore before we release.
                    await gate.Task;
                    return new Payload("single-flight", 1);
                },
                TimeSpan.FromMinutes(1));
        })).ToArray();

        await Task.Delay(100); // let every task reach the coalescer
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, invocations);
        Assert.All(results, r => Assert.Equal("single-flight", r!.Name));
    }

    public record Payload(string Name, int Value);
}
