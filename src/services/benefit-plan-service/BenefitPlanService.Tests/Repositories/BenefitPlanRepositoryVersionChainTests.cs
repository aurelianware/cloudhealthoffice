using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Tests.Fakes;

namespace BenefitPlanService.Tests.Repositories;

/// <summary>
/// Contract tests for the version-chain repository semantics. Runs against
/// the in-memory fake — the Cosmos and Mongo implementations enforce the
/// same invariants but require live infrastructure to exercise.
/// </summary>
public class BenefitPlanRepositoryVersionChainTests
{
    private const string Tenant = "t1";

    private static BenefitPlan Draft(string planId, int n = 1, string? predecessor = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        PlanId = planId,
        PlanName = "Draft",
        Payer = "Acme",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.HMO,
        LineOfBusiness = LineOfBusiness.Commercial,
        VersionId = PlanVersionId.NewId(),
        VersionNumber = n,
        VersionState = PlanVersionState.Draft,
        PredecessorVersionId = predecessor
    };

    [Fact]
    public async Task UpdateAsync_against_published_throws_state_exception()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var plan = Draft("plan-1");
        plan.VersionState = PlanVersionState.Published;
        await repo.CreateAsync(plan);

        var act = () => repo.UpdateAsync(plan);
        await act.Should().ThrowAsync<PlanVersionStateException>();
    }

    [Fact]
    public async Task UpdateDraftAsync_rejects_published_target()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var plan = Draft("plan-1");
        plan.VersionState = PlanVersionState.Published;
        await repo.CreateAsync(plan);

        plan.VersionState = PlanVersionState.Draft; // attempt to "downgrade"
        var act = () => repo.UpdateDraftAsync(plan);
        await act.Should().ThrowAsync<PlanVersionStateException>();
    }

    [Fact]
    public async Task GetLatestPublishedAsync_excludes_drafts_and_superseded()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var d = Draft("plan-1");
        var p = Draft("plan-1", 2);
        p.VersionState = PlanVersionState.Published;
        var s = Draft("plan-1", 3);
        s.VersionState = PlanVersionState.Superseded;
        await repo.CreateAsync(d);
        await repo.CreateAsync(p);
        await repo.CreateAsync(s);

        var latest = await repo.GetLatestPublishedAsync("plan-1", Tenant, DateTime.UtcNow);
        latest!.VersionId.Should().Be(p.VersionId);
    }

    [Fact]
    public async Task GetLatestPublishedAsync_respects_asOf_window()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var p = Draft("plan-1");
        p.VersionState = PlanVersionState.Published;
        p.EffectiveDate = new DateTime(2026, 6, 1);
        p.TerminationDate = new DateTime(2026, 12, 31);
        await repo.CreateAsync(p);

        (await repo.GetLatestPublishedAsync("plan-1", Tenant, new DateTime(2026, 1, 1))).Should().BeNull();
        (await repo.GetLatestPublishedAsync("plan-1", Tenant, new DateTime(2026, 7, 1))).Should().NotBeNull();
        (await repo.GetLatestPublishedAsync("plan-1", Tenant, new DateTime(2027, 1, 1))).Should().BeNull();
    }

    [Fact]
    public async Task PublishAndSupersedeAsync_rolls_back_on_simulated_failure()
    {
        var repo = new InMemoryBenefitPlanRepository { FailNextPublish = true };
        var p = Draft("plan-1");
        p.VersionState = PlanVersionState.Published;
        await repo.CreateAsync(p);

        var d = Draft("plan-1", 2, p.VersionId);
        await repo.CreateDraftAsync(d);

        d.VersionState = PlanVersionState.Published;
        var pred = await repo.GetByIdAsync(p.Id, Tenant);
        pred!.VersionState = PlanVersionState.Superseded;
        pred.SupersededByVersionId = d.VersionId;

        var act = () => repo.PublishAndSupersedeAsync(d, pred);
        await act.Should().ThrowAsync<PlanVersionStateException>();

        var existing = await repo.GetByIdAsync(p.Id, Tenant);
        existing!.VersionState.Should().Be(PlanVersionState.Published);
    }

    [Fact]
    public async Task ListVersionsAsync_paginates_newest_first()
    {
        var repo = new InMemoryBenefitPlanRepository();
        for (var i = 1; i <= 5; i++)
        {
            var v = Draft("plan-1", i);
            await repo.CreateAsync(v);
        }

        var (items, next) = await repo.ListVersionsAsync("plan-1", Tenant, 2, null);
        items.Select(v => v.VersionNumber).Should().Equal(5, 4);
        next.Should().NotBeNull();

        var (page2, next2) = await repo.ListVersionsAsync("plan-1", Tenant, 2, next);
        page2.Select(v => v.VersionNumber).Should().Equal(3, 2);
        next2.Should().NotBeNull();

        var (page3, next3) = await repo.ListVersionsAsync("plan-1", Tenant, 2, next2);
        page3.Select(v => v.VersionNumber).Should().Equal(1);
        next3.Should().BeNull();
    }
}
