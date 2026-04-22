using CloudHealthOffice.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;

namespace CloudHealthOffice.Infrastructure.Tests.Caching;

public class InMemoryCacheProviderTests : CacheProviderContractTests
{
    protected override ValueTask<(ICacheProvider, IDisposable?)> CreateCacheAsync()
    {
        var cache       = new MemoryCache(new MemoryCacheOptions());
        var singleFlight = new SingleFlightRunner(maxInFlight: 1024);
        var provider     = new InMemoryCacheProvider(cache, singleFlight);

        return ValueTask.FromResult<(ICacheProvider, IDisposable?)>(
            (provider, new Disposables(cache, singleFlight)));
    }

    private sealed class Disposables : IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly SingleFlightRunner _sf;
        public Disposables(MemoryCache cache, SingleFlightRunner sf) { _cache = cache; _sf = sf; }
        public void Dispose() { _sf.Dispose(); _cache.Dispose(); }
    }
}
