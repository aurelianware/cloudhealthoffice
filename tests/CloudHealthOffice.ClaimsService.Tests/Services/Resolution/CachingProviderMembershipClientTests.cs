using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

/// <summary>
/// Capability 5.6 — caching behaviour for the network-membership client.
/// Mirrors <see cref="CachingBenefitPlanResolverTests"/> shape but
/// extends to the asOf-day cache key and the
/// <c>cached-or-live</c> vs <c>force-refresh</c> namespace separation.
/// </summary>
public class CachingProviderMembershipClientTests
{
    private readonly IProviderMembershipClient _inner = Substitute.For<IProviderMembershipClient>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private const string TenantId = "tenant-1";
    private const string NetworkId = "net-1";
    private const string Npi = "1234567890";
    private static readonly DateTime AsOf = new(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CacheHit_does_not_call_inner_a_second_time()
    {
        var sut = new CachingProviderMembershipClient(_inner, _cache);
        var membership = new NetworkMembership
        {
            NetworkId = NetworkId, Npi = Npi, IsActiveMember = true, AsOfDate = AsOf,
        };
        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(membership);

        var first = await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);
        var second = await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);

        Assert.Same(membership, first);
        Assert.Same(membership, second);
        await _inner.Received(1)
            .GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullResult_is_not_cached()
    {
        var sut = new CachingProviderMembershipClient(_inner, _cache);
        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>())
            .Returns((NetworkMembership?)null,
                     new NetworkMembership { NetworkId = NetworkId, Npi = Npi, AsOfDate = AsOf });

        var first = await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);
        var second = await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);

        Assert.Null(first);
        Assert.NotNull(second);
        await _inner.Received(2)
            .GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceRefresh_uses_separate_cache_key()
    {
        var sut = new CachingProviderMembershipClient(_inner, _cache);
        var cached = new NetworkMembership { NetworkId = NetworkId, Npi = Npi, IsActiveMember = true, AsOfDate = AsOf };
        var live = new NetworkMembership { NetworkId = NetworkId, Npi = Npi, IsActiveMember = false, AsOfDate = AsOf };

        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(cached);
        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, true, Arg.Any<CancellationToken>())
            .Returns(live);

        // Prime the cached path.
        await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);

        // Force-refresh hits inner with forceRefresh=true, bypassing the
        // cached-or-live entry.
        var forced = await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, forceRefresh: true);

        Assert.NotSame(cached, forced);
        Assert.False(forced!.IsActiveMember);
    }

    [Fact]
    public async Task DifferentDays_do_not_share_cache_entries()
    {
        var sut = new CachingProviderMembershipClient(_inner, _cache);
        var d1 = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2025, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, d1, false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { NetworkId = NetworkId, Npi = Npi, AsOfDate = d1 });
        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, d2, false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { NetworkId = NetworkId, Npi = Npi, AsOfDate = d2 });

        await sut.GetMembershipAsync(TenantId, NetworkId, Npi, d1);
        await sut.GetMembershipAsync(TenantId, NetworkId, Npi, d2);

        // Each day-bucketed key triggered a fetch.
        await _inner.Received(1)
            .GetMembershipAsync(TenantId, NetworkId, Npi, d1, false, Arg.Any<CancellationToken>());
        await _inner.Received(1)
            .GetMembershipAsync(TenantId, NetworkId, Npi, d2, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtlZero_disables_cache()
    {
        var sut = new CachingProviderMembershipClient(_inner, _cache, TimeSpan.Zero);
        _inner.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { NetworkId = NetworkId, Npi = Npi, AsOfDate = AsOf });

        await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);
        await sut.GetMembershipAsync(TenantId, NetworkId, Npi, AsOf);

        await _inner.Received(2)
            .GetMembershipAsync(TenantId, NetworkId, Npi, AsOf, false, Arg.Any<CancellationToken>());
    }
}
