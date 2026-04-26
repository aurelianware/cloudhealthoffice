using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;

namespace BenefitPlanService.Tests.Services;

public class FakePlanVersionEventPublisherTests
{
    [Fact]
    public async Task PublishVersionPublished_records_event_with_correct_envelope()
    {
        var sut = new FakePlanVersionEventPublisher();
        var version = new BenefitPlan
        {
            TenantId = "t1",
            PlanId = "plan-1",
            VersionId = "v-1",
            VersionNumber = 1,
            VersionState = PlanVersionState.Published
        };

        var evt = await sut.PublishVersionPublishedAsync(version, "actor", null);

        evt.EventType.Should().Be(PlanVersionEventType.PlanVersionPublished);
        evt.PlanId.Should().Be("plan-1");
        evt.VersionId.Should().Be("v-1");
        evt.Version.Should().Be(1);
    }

    [Fact]
    public async Task PublishVersionSuperseded_records_pair_with_monotonic_version()
    {
        var sut = new FakePlanVersionEventPublisher();
        var v1 = new BenefitPlan { TenantId = "t1", PlanId = "p", VersionId = "v1" };
        var v2 = new BenefitPlan { TenantId = "t1", PlanId = "p", VersionId = "v2" };

        await sut.PublishVersionPublishedAsync(v2, "a", null);
        await sut.PublishVersionSupersededAsync(v1, v2, "amend", "a", null);

        sut.Events.Should().HaveCount(2);
        sut.Events[0].Version.Should().Be(1);
        sut.Events[1].Version.Should().Be(2);
        sut.Events[1].EventType.Should().Be(PlanVersionEventType.PlanVersionSuperseded);
    }
}
