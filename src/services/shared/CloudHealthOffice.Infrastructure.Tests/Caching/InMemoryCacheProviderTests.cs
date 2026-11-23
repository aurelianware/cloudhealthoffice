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

    [Fact]
    public async Task Works_in_a_size_limited_memory_cache()
    {
        // fhir-service registers AddMemoryCache(SizeLimit = 1024); an entry
        // without a size threw there, and CRD fell back to "could not be determined".
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 16 });
        using var singleFlight = new SingleFlightRunner(maxInFlight: 16);
        var provider = new InMemoryCacheProvider(cache, singleFlight);

        await provider.SetAsync("k", "v", TimeSpan.FromMinutes(1));

        (await provider.GetAsync<string>("k")).Should().Be("v");
    }

    private sealed class Disposables : IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly SingleFlightRunner _sf;
        public Disposables(MemoryCache cache, SingleFlightRunner sf) { _cache = cache; _sf = sf; }
        public void Dispose() { _sf.Dispose(); _cache.Dispose(); }
    }
}
