using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.7 — verifies <see cref="BenefitViewService"/> projects
/// <c>BenefitPlan.FamilyAccumulatorModel</c> onto the
/// <c>MemberBenefitView</c> as a string. Portal DTO mirroring is covered
/// by adapter round-trip tests; this fixture exercises the service-side
/// projection only.
/// </summary>
public sealed class BenefitViewServiceFamilyModelTests
{
    private static (BenefitViewService view, BenefitPlanServiceImpl service,
        InMemoryBenefitPlanRepository repo) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var transitions = new InMemoryPlanVersionTransitionRepository();
        var events = new FakePlanVersionEventPublisher();
        var service = new BenefitPlanServiceImpl(
            repo, transitions, events,
            new NoOpNetworkTierSoftValidator(),
            new NoOpPlanLimitValidator(),
            NullLogger<BenefitPlanServiceImpl>.Instance);
        var view = new BenefitViewService(service, NullLogger<BenefitViewService>.Instance);
        return (view, service, repo);
    }

    private static BenefitPlan SamplePlan(FamilyAccumulatorModel model) => new()
    {
        Id = "plan-row-001",
        TenantId = "tenant-a",
        PlanId = "plan-001",
        PlanName = "ACA Test",
        Payer = "Acme",
        EffectiveDate = new DateTime(2025, 1, 1),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        FamilyAccumulatorModel = model,
        VersionId = "v1",
        VersionNumber = 1,
        VersionState = PlanVersionState.Published,
        IsActive = true,
        Benefits = new(),
    };

    [Fact]
    public async Task GetMemberView_Surfaces_Embedded_Model_String()
    {
        var (view, _, repo) = Build();
        var plan = SamplePlan(FamilyAccumulatorModel.Embedded);
        await repo.CreateAsync(plan);

        var result = await view.GetMemberViewAsync(plan.PlanId, plan.TenantId, DateTime.UtcNow);

        result.Should().NotBeNull();
        result!.FamilyAccumulatorModel.Should().Be("Embedded");
    }

    [Fact]
    public async Task GetMemberView_Surfaces_Aggregate_Model_String()
    {
        var (view, _, repo) = Build();
        var plan = SamplePlan(FamilyAccumulatorModel.Aggregate);
        await repo.CreateAsync(plan);

        var result = await view.GetMemberViewAsync(plan.PlanId, plan.TenantId, DateTime.UtcNow);

        result.Should().NotBeNull();
        result!.FamilyAccumulatorModel.Should().Be("Aggregate");
    }
}
