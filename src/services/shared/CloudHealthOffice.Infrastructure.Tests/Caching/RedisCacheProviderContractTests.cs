using CloudHealthOffice.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Caching;

/// <summary>
/// Real-Redis contract fixture. Skipped unless
/// <c>CHO_REDIS_CONNECTION_STRING</c> is set in the environment, mirroring
/// the Service Bus pattern (<c>CHO_SERVICEBUS_CONNECTION_STRING</c>) and
/// keeping CI green on workers that have no Redis available.
/// </summary>
[Trait("Category", "Integration")]
public class RedisCacheProviderContractTests : CacheProviderContractTests
{
    private const string EnvVar = "CHO_REDIS_CONNECTION_STRING";

    protected override async ValueTask<(ICacheProvider, IDisposable?)> CreateCacheAsync()
    {
        var cs = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(cs), $"{EnvVar} not set; skipping real-Redis contract tests.");

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(cs!);
        var singleFlight = new SingleFlightRunner(maxInFlight: 1024);
        var provider = new RedisCacheProvider(
            multiplexer,
            singleFlight,
            NullLogger<RedisCacheProvider>.Instance);

        return (provider, new Disposables(multiplexer, singleFlight));
    }

    private sealed class Disposables : IDisposable
    {
        private readonly ConnectionMultiplexer _mx;
        private readonly SingleFlightRunner _sf;
        public Disposables(ConnectionMultiplexer mx, SingleFlightRunner sf) { _mx = mx; _sf = sf; }
        public void Dispose() { _sf.Dispose(); _mx.Dispose(); }
    }
}
