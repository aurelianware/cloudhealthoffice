using CloudHealthOffice.Infrastructure.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.Infrastructure.Tests.Caching;

public class CachingOptionsAutoResolutionTests
{
    private const string FakeRedisConnection = "fake.redis:6379,abortConnect=false";

    [Fact]
    public void Auto_InDev_ResolvesToInMemory()
    {
        var decision = CachingServiceCollectionExtensions.ResolveBackend(
            new CachingOptions { Backend = "Auto" }, Env("Development"));

        Assert.Equal(CachingServiceCollectionExtensions.CachingBackend.InMemory, decision.Backend);
        Assert.Contains("Development", decision.Reason);
    }

    [Fact]
    public void Auto_InProdWithoutConnectionString_ResolvesToInMemory()
    {
        var decision = CachingServiceCollectionExtensions.ResolveBackend(
            new CachingOptions { Backend = "Auto" }, Env("Production"));

        Assert.Equal(CachingServiceCollectionExtensions.CachingBackend.InMemory, decision.Backend);
        Assert.Contains("no ConnectionString", decision.Reason);
    }

    [Fact]
    public void Auto_InProdWithConnectionString_ResolvesToRedis()
    {
        var decision = CachingServiceCollectionExtensions.ResolveBackend(
            new CachingOptions { Backend = "Auto", RedisConnectionString = FakeRedisConnection },
            Env("Production"));

        Assert.Equal(CachingServiceCollectionExtensions.CachingBackend.Redis, decision.Backend);
        Assert.Contains("ConnectionString configured", decision.Reason);
    }

    [Fact]
    public void ExplicitRedis_WithoutConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CachingServiceCollectionExtensions.ResolveBackend(
                new CachingOptions { Backend = "Redis" }, Env("Production")));
    }

    [Fact]
    public void ExplicitInMemory_IsHonouredInProduction()
    {
        var decision = CachingServiceCollectionExtensions.ResolveBackend(
            new CachingOptions { Backend = "InMemory" }, Env("Production"));

        Assert.Equal(CachingServiceCollectionExtensions.CachingBackend.InMemory, decision.Backend);
        Assert.Contains("forced", decision.Reason);
    }

    [Fact]
    public void ExplicitNull_IsHonoured()
    {
        var decision = CachingServiceCollectionExtensions.ResolveBackend(
            new CachingOptions { Backend = "Null" }, Env("Production"));

        Assert.Equal(CachingServiceCollectionExtensions.CachingBackend.Null, decision.Backend);
    }

    [Fact]
    public void UnknownBackend_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CachingServiceCollectionExtensions.ResolveBackend(
                new CachingOptions { Backend = "Bogus" }, Env("Production")));
    }

    [Fact]
    public void LegacyRedisConnectionString_IsFallback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:Backend"]        = "Auto",
            ["Redis:ConnectionString"] = FakeRedisConnection
        }).Build();

        var (cs, deprecated) = CachingServiceCollectionExtensions
            .ResolveConnectionString(config, new CachingOptions { Backend = "Auto" });

        Assert.Equal(FakeRedisConnection, cs);
        Assert.Equal("Redis:ConnectionString", deprecated);
    }

    [Fact]
    public void CanonicalKey_WinsOverLegacyFallback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:RedisConnectionString"] = FakeRedisConnection,
            ["Redis:ConnectionString"]        = "legacy-value"
        }).Build();

        var (cs, deprecated) = CachingServiceCollectionExtensions
            .ResolveConnectionString(config, new CachingOptions
            {
                Backend               = "Auto",
                RedisConnectionString = FakeRedisConnection
            });

        Assert.Equal(FakeRedisConnection, cs);
        Assert.Null(deprecated);
    }

    [Fact]
    public async Task AddChoCaching_RegistersSingleton_InMemoryPath()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:Backend"] = "InMemory"
        }).Build();

        services.AddChoCaching(config, Env("Development"));
        await using var sp = services.BuildServiceProvider();

        var c1 = sp.GetRequiredService<ICacheProvider>();
        var c2 = sp.GetRequiredService<ICacheProvider>();
        Assert.Same(c1, c2);
        Assert.IsType<GuardedCacheProvider>(c1);
    }

    [Fact]
    public async Task AddChoCaching_RegistersNullProvider_WhenForced()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:Backend"] = "Null"
        }).Build();

        services.AddChoCaching(config, Env("Development"));
        await using var sp = services.BuildServiceProvider();

        var cache = sp.GetRequiredService<ICacheProvider>();
        Assert.IsType<GuardedCacheProvider>(cache);
        // Round-trip a set-then-get: null backend means the get always
        // misses even right after a set.
        await cache.SetAsync("k", "v", TimeSpan.FromMinutes(1), CacheScope.Global);
        Assert.Null(await cache.GetAsync<string>("k", CacheScope.Global));
    }

    private static IHostEnvironment Env(string name) => new FakeEnv { EnvironmentName = name };

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "cho-test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
