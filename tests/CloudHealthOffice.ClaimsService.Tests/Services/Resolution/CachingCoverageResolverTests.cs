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

    [Fact]
    public async Task Resolve_CacheMiss_HitsInner_AndCachesResult()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns("plan-guid-123");

        var first = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var second = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT");

        Assert.Equal("plan-guid-123", first);
        Assert.Equal("plan-guid-123", second);
        await _inner.Received(1).ResolveBenefitPlanIdAsync(
            "tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_NullResult_NotCached()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns((string?)null, "plan-guid-123");

        var first = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var second = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT");

        Assert.Null(first);
        Assert.Equal("plan-guid-123", second);
        await _inner.Received(2).ResolveBenefitPlanIdAsync(
            "tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolve_DifferentInsuranceLineCodes_DoNotShareCacheEntries()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        _inner.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns("medical-plan");
        _inner.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "DEN", Arg.Any<CancellationToken>())
            .Returns("dental-plan");

        var medical = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var dental = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "DEN");

        Assert.Equal("medical-plan", medical);
        Assert.Equal("dental-plan", dental);
    }

    [Fact]
    public async Task Resolve_DifferentServiceDates_DoNotShareCacheEntries()
    {
        var sut = new CachingCoverageResolver(_inner, _cache);
        var laterDate = ServiceDate.AddYears(1);
        _inner.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT", Arg.Any<CancellationToken>())
            .Returns("old-plan");
        _inner.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", laterDate, "HLT", Arg.Any<CancellationToken>())
            .Returns("new-plan");

        var old = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", ServiceDate, "HLT");
        var current = await sut.ResolveBenefitPlanIdAsync("tenant-1", "MEM-1", laterDate, "HLT");

        Assert.Equal("old-plan", old);
        Assert.Equal("new-plan", current);
    }
}
