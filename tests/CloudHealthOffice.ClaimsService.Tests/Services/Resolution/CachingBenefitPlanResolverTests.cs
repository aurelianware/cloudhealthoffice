using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

public class CachingBenefitPlanResolverTests
{
    private readonly IBenefitPlanResolver _inner = Substitute.For<IBenefitPlanResolver>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    [Fact]
    public async Task GetPlan_CacheMiss_HitsInner_AndCachesResult()
    {
        var sut = new CachingBenefitPlanResolver(_inner, _cache);
        var plan = new ResolvedBenefitPlan { Id = "p1", PlanName = "Gold PPO" };
        _inner.GetPlanAsync("tenant-1", "p1", Arg.Any<CancellationToken>()).Returns(plan);

        var first = await sut.GetPlanAsync("tenant-1", "p1");
        var second = await sut.GetPlanAsync("tenant-1", "p1");

        Assert.Same(plan, first);
        Assert.Same(plan, second);
        await _inner.Received(1).GetPlanAsync("tenant-1", "p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPlan_NullResult_NotCached()
    {
        var sut = new CachingBenefitPlanResolver(_inner, _cache);
        _inner.GetPlanAsync("tenant-1", "p1", Arg.Any<CancellationToken>())
            .Returns((ResolvedBenefitPlan?)null, new ResolvedBenefitPlan { Id = "p1" });

        var first = await sut.GetPlanAsync("tenant-1", "p1");
        var second = await sut.GetPlanAsync("tenant-1", "p1");

        Assert.Null(first);
        Assert.NotNull(second);
        await _inner.Received(2).GetPlanAsync("tenant-1", "p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPlan_DifferentTenants_DoNotShareCacheEntries()
    {
        var sut = new CachingBenefitPlanResolver(_inner, _cache);
        var planA = new ResolvedBenefitPlan { Id = "p1", PlanName = "A" };
        var planB = new ResolvedBenefitPlan { Id = "p1", PlanName = "B" };
        _inner.GetPlanAsync("tenant-A", "p1", Arg.Any<CancellationToken>()).Returns(planA);
        _inner.GetPlanAsync("tenant-B", "p1", Arg.Any<CancellationToken>()).Returns(planB);

        var a = await sut.GetPlanAsync("tenant-A", "p1");
        var b = await sut.GetPlanAsync("tenant-B", "p1");

        Assert.Equal("A", a?.PlanName);
        Assert.Equal("B", b?.PlanName);
    }

    [Fact]
    public async Task GetPlan_TtlZero_DisablesCache()
    {
        var sut = new CachingBenefitPlanResolver(_inner, _cache, TimeSpan.Zero);
        _inner.GetPlanAsync("tenant-1", "p1", Arg.Any<CancellationToken>())
            .Returns(new ResolvedBenefitPlan { Id = "p1" });

        await sut.GetPlanAsync("tenant-1", "p1");
        await sut.GetPlanAsync("tenant-1", "p1");

        await _inner.Received(2).GetPlanAsync("tenant-1", "p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPlan_ConcurrentCacheMisses_AreCoalesced()
    {
        var sut = new CachingBenefitPlanResolver(_inner, _cache);
        var release = new TaskCompletionSource<ResolvedBenefitPlan?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _inner.GetPlanAsync("tenant-1", "new-plan", Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);

        var requests = Enumerable.Range(0, 20)
            .Select(_ => sut.GetPlanAsync("tenant-1", "new-plan"))
            .ToArray();

        await _inner.Received(1)
            .GetPlanAsync("tenant-1", "new-plan", Arg.Any<CancellationToken>());
        release.SetResult(new ResolvedBenefitPlan { Id = "new-plan" });

        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.Equal("new-plan", result?.Id));
        await _inner.Received(1)
            .GetPlanAsync("tenant-1", "new-plan", Arg.Any<CancellationToken>());
    }
}
