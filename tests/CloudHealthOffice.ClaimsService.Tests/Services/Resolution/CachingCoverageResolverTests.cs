using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

public class CachingCoverageResolverTests
{
    private readonly ICoverageResolver _inner = Substitute.For<ICoverageResolver>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private static readonly DateTime ServiceDate = new(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);

    private static CoverageResolution Plan(string planId) => new(planId);

    [Fact]
    public async Task Resolve_CacheMiss_HitsInner_AndCachesResult()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns(Plan("plan-guid-123"));

        var first = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var second = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");

        Assert.Equal("plan-guid-123", first?.PlanId);
        Assert.Equal("plan-guid-123", second?.PlanId);
        await _inner.Received(1).ResolveCoverageAsync(
            "tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_NullResult_NotCached()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns((CoverageResolution?)null, Plan("plan-guid-123"));

        var first = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var second = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");

        Assert.Null(first);
        Assert.Equal("plan-guid-123", second?.PlanId);
        await _inner.Received(2).ResolveCoverageAsync(
            "tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_DifferentInsuranceLineCodes_DoNotShareCacheEntries()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns(Plan("medical-plan"));
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "DEN", Arg.Any<CancellationToken>())
            .Returns(Plan("dental-plan"));

        var medical = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var dental = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "DEN");

        Assert.Equal("medical-plan", medical?.PlanId);
        Assert.Equal("dental-plan", dental?.PlanId);
    }

    [Fact]
    public async Task Resolve_DifferentServiceDates_DoNotShareCacheEntries()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        var laterDate = ServiceDate.AddYears(1);
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns(Plan("old-plan"));
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", laterDate, "HLT", Arg.Any<CancellationToken>())
            .Returns(Plan("new-plan"));

        var old = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var current = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", laterDate, "HLT");

        Assert.Equal("old-plan", old?.PlanId);
        Assert.Equal("new-plan", current?.PlanId);
    }

    [Fact]
    public async Task A_cache_hit_still_carries_the_sponsor_and_policy()
    {
        // The cached value used to be the plan id alone. If only that were
        // cached now, the first claim for a member would resolve settings at
        // the sponsor's policy and every claim behind it, for five minutes,
        // would silently fall back to the tenant default.
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns(new CoverageResolution("plan-1", "GRP0001", "POL-9"));

        await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var cached = await sut.ResolveCoverageAsync("tenant-1", "MEM-1", ServiceDate, "HLT");

        Assert.Equal("GRP0001", cached?.GroupNumber);
        Assert.Equal("POL-9", cached?.SponsorPolicyNumber);
        await _inner.Received(1).ResolveCoverageAsync(
            "tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>());
    }
}
