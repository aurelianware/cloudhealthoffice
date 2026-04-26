using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;

namespace BenefitPlanService.Tests.Services;

public class FakePlanYearTransitionPublisherTests
{
    private static BenefitPlan SamplePlan(string planId = "plan-A") => new()
    {
        TenantId = "tenant-1",
        PlanId = planId,
        PlanName = "Test",
        Payer = "Test",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.PPO,
        VersionId = "v-1",
        VersionNumber = 1,
        VersionState = PlanVersionState.Published,
        PlanYearDefinition = new PlanYearDefinition
        {
            PlanYearStart = new DateTime(2026, 1, 1),
            PlanYearEnd = new DateTime(2026, 12, 31),
            PlanYearType = PlanYearType.CalendarYear
        }
    };

    [Fact]
    public async Task PublishApproaching_then_PublishTransition_uses_monotonic_versions()
    {
        var sut = new FakePlanYearTransitionPublisher();
        var plan = SamplePlan();
        var end = new DateTime(2026, 12, 31);
        var nextStart = end.AddDays(1);

        var a = await sut.PublishApproachingAsync(plan, end, nextStart, "scheduler", null);
        var t = await sut.PublishTransitionAsync(plan, end, nextStart, "scheduler", null);

        sut.Events.Should().HaveCount(2);
        a.Version.Should().Be(1);
        t.Version.Should().Be(2);
        a.TransitionType.Should().Be(PlanYearTransitionType.ApproachingTransition);
        t.TransitionType.Should().Be(PlanYearTransitionType.Transition);
    }

    [Fact]
    public async Task Republishing_same_transition_is_idempotent()
    {
        // Two scheduler replicas racing — both compute the same EventId
        // and the publisher must collapse them to one row.
        var sut = new FakePlanYearTransitionPublisher();
        var plan = SamplePlan();
        var end = new DateTime(2026, 12, 31);
        var nextStart = end.AddDays(1);

        var first = await sut.PublishTransitionAsync(plan, end, nextStart, "scheduler", "corr-1");
        var second = await sut.PublishTransitionAsync(plan, end, nextStart, "scheduler", "corr-2");

        sut.Events.Should().ContainSingle();
        first.EventId.Should().Be(second.EventId);
        // First write wins — second call returns the original row, so
        // the original CorrelationId is preserved.
        second.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task Different_planYearEnds_produce_distinct_events()
    {
        var sut = new FakePlanYearTransitionPublisher();
        var plan = SamplePlan();

        await sut.PublishTransitionAsync(plan, new DateTime(2025, 12, 31), new DateTime(2026, 1, 1), null, null);
        await sut.PublishTransitionAsync(plan, new DateTime(2026, 12, 31), new DateTime(2027, 1, 1), null, null);

        sut.Events.Should().HaveCount(2);
        sut.Events.Select(e => e.EventId).Should().OnlyHaveUniqueItems();
    }
}
