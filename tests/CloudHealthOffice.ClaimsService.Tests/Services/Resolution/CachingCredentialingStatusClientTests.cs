using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

/// <summary>
/// Capability 5.6 — caching behaviour for the credentialing-status
/// client. Mirrors <see cref="CachingProviderMembershipClientTests"/>
/// shape; the longer 1-hour TTL is the only intentional asymmetry.
/// </summary>
public class CachingCredentialingStatusClientTests
{
    private readonly ICredentialingStatusClient _inner = Substitute.For<ICredentialingStatusClient>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private const string TenantId = "tenant-1";
    private const string ProviderId = "p-001";
    private static readonly DateTime AsOf = new(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CacheHit_does_not_call_inner_a_second_time()
    {
        var sut = new CachingCredentialingStatusClient(_inner, _cache);
        var snap = new CredentialingStatusSnapshot
        {
            ProviderId = ProviderId, AsOfDate = AsOf, Status = "Approved",
        };
        _inner.GetStatusAsOfAsync(TenantId, ProviderId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(snap);

        var first = await sut.GetStatusAsOfAsync(TenantId, ProviderId, AsOf);
        var second = await sut.GetStatusAsOfAsync(TenantId, ProviderId, AsOf);

        Assert.Same(snap, first);
        Assert.Same(snap, second);
        await _inner.Received(1)
            .GetStatusAsOfAsync(TenantId, ProviderId, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullResult_is_not_cached()
    {
        var sut = new CachingCredentialingStatusClient(_inner, _cache);
        _inner.GetStatusAsOfAsync(TenantId, ProviderId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns((CredentialingStatusSnapshot?)null,
                     new CredentialingStatusSnapshot { ProviderId = ProviderId, AsOfDate = AsOf, Status = "Approved" });

        var first = await sut.GetStatusAsOfAsync(TenantId, ProviderId, AsOf);
        var second = await sut.GetStatusAsOfAsync(TenantId, ProviderId, AsOf);

        Assert.Null(first);
        Assert.NotNull(second);
        await _inner.Received(2)
            .GetStatusAsOfAsync(TenantId, ProviderId, AsOf, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceRefresh_bypasses_cache()
    {
        var sut = new CachingCredentialingStatusClient(_inner, _cache);
        var cachedSnap = new CredentialingStatusSnapshot { ProviderId = ProviderId, AsOfDate = AsOf, Status = "Approved" };
        var liveSnap = new CredentialingStatusSnapshot { ProviderId = ProviderId, AsOfDate = AsOf, Status = "Suspended" };

        _inner.GetStatusAsOfAsync(TenantId, ProviderId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(cachedSnap);
        _inner.GetStatusAsOfAsync(TenantId, ProviderId, AsOf, true, Arg.Any<CancellationToken>())
            .Returns(liveSnap);

        await sut.GetStatusAsOfAsync(TenantId, ProviderId, AsOf);
        var forced = await sut.GetStatusAsOfAsync(TenantId, ProviderId, AsOf, forceRefresh: true);

        Assert.Equal("Suspended", forced!.Status);
    }

    [Fact]
    public async Task DifferentTenants_do_not_share_cache_entries()
    {
        var sut = new CachingCredentialingStatusClient(_inner, _cache);
        _inner.GetStatusAsOfAsync("tenant-A", ProviderId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot { ProviderId = ProviderId, AsOfDate = AsOf, Status = "Approved" });
        _inner.GetStatusAsOfAsync("tenant-B", ProviderId, AsOf, false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot { ProviderId = ProviderId, AsOfDate = AsOf, Status = "Suspended" });

        var a = await sut.GetStatusAsOfAsync("tenant-A", ProviderId, AsOf);
        var b = await sut.GetStatusAsOfAsync("tenant-B", ProviderId, AsOf);

        Assert.Equal("Approved", a!.Status);
        Assert.Equal("Suspended", b!.Status);
    }
}
