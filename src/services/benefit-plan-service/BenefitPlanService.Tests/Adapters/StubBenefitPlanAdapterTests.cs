using BenefitPlanService.Adapters;
using BenefitPlanService.Models;

namespace BenefitPlanService.Tests.Adapters;

public class StubBenefitPlanAdapterTests
{
    public static IEnumerable<object[]> Stubs() => new[]
    {
        new object[] { new QnxtBenefitPlanAdapter(), "qnxt", "qnxt-benefit-plan" },
        new object[] { new FacetsBenefitPlanAdapter(), "facets", "facets-benefit-plan" },
        new object[] { new HealthEdgeBenefitPlanAdapter(), "healthedge", "healthedge-benefit-plan" },
    };

    [Theory]
    [MemberData(nameof(Stubs))]
    public void Platform_returns_expected_string(IBenefitPlanAdapter adapter, string expected, string _)
    {
        adapter.Platform.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GetPlanAsync_throws_with_migration_TODO(IBenefitPlanAdapter adapter, string _, string todoMarker)
    {
        var request = new BenefitPlanAdapterRequest { TenantId = "t", PlanId = "p" };
        var act = () => adapter.GetPlanAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain("TODO");
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GetPlanVersionAsync_throws_with_migration_TODO(IBenefitPlanAdapter adapter, string _, string todoMarker)
    {
        var request = new BenefitPlanAdapterRequest { TenantId = "t", PlanId = "p", VersionId = "v" };
        var act = () => adapter.GetPlanVersionAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain("TODO");
        ex.Which.Message.Should().Contain(todoMarker);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task GetMemberBenefitViewAsync_throws_with_migration_TODO(IBenefitPlanAdapter adapter, string _, string todoMarker)
    {
        var request = new BenefitPlanAdapterRequest { TenantId = "t", PlanId = "p" };
        var act = () => adapter.GetMemberBenefitViewAsync(request);

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain("TODO");
        ex.Which.Message.Should().Contain(todoMarker);
    }
}
