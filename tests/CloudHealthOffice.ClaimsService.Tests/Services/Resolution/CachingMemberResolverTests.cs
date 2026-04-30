using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

public class CachingMemberResolverTests
{
    private readonly IMemberResolver _inner = Substitute.For<IMemberResolver>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public async Task GetMember_CacheMiss_HitsInner_AndCachesResult()
    {
        var sut = new CachingMemberResolver(_inner, _cache);
        var member = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true };
        _inner.GetMemberAsync("tenant-1", "MEM-1", Arg.Any<CancellationToken>()).Returns(member);

        var first = await sut.GetMemberAsync("tenant-1", "MEM-1");
        var second = await sut.GetMemberAsync("tenant-1", "MEM-1");

        Assert.Same(member, first);
        Assert.Same(member, second);
        await _inner.Received(1).GetMemberAsync("tenant-1", "MEM-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMember_NullResult_NotCached()
    {
        var sut = new CachingMemberResolver(_inner, _cache);
        _inner.GetMemberAsync("tenant-1", "MEM-1", Arg.Any<CancellationToken>())
            .Returns(
                (ResolvedMember?)null,
                new ResolvedMember { MemberId = "MEM-1" });

        var first = await sut.GetMemberAsync("tenant-1", "MEM-1");
        var second = await sut.GetMemberAsync("tenant-1", "MEM-1");

        Assert.Null(first);
        Assert.NotNull(second);
        await _inner.Received(2).GetMemberAsync("tenant-1", "MEM-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMember_DifferentMembers_DoNotShareCacheEntries()
    {
        var sut = new CachingMemberResolver(_inner, _cache);
        _inner.GetMemberAsync("tenant-1", "MEM-1", Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember { MemberId = "MEM-1" });
        _inner.GetMemberAsync("tenant-1", "MEM-2", Arg.Any<CancellationToken>())
            .Returns(new ResolvedMember { MemberId = "MEM-2" });

        var a = await sut.GetMemberAsync("tenant-1", "MEM-1");
        var b = await sut.GetMemberAsync("tenant-1", "MEM-2");

        Assert.Equal("MEM-1", a?.MemberId);
        Assert.Equal("MEM-2", b?.MemberId);
    }
}
