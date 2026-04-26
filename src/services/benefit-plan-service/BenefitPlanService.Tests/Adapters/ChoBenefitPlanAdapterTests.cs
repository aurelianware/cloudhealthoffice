using BenefitPlanService.Adapters;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Adapters;

public class ChoBenefitPlanAdapterTests
{
    private const string Tenant = "tenant-a";

    [Fact]
    public void Platform_is_cho()
    {
        var adapter = new ChoBenefitPlanAdapter(
            Mock.Of<IBenefitPlanService>(),
            Mock.Of<IBenefitViewService>(),
            NullLogger<ChoBenefitPlanAdapter>.Instance);

        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetPlanAsync_delegates_to_plan_service_and_maps_result()
    {
        var plan = new BenefitPlan
        {
            Id = "id-1",
            TenantId = Tenant,
            PlanId = "plan-x",
            PlanName = "Plan X",
            Payer = "Acme",
            EffectiveDate = new DateTime(2026, 1, 1),
            PlanType = PlanType.PPO,
        };
        var planService = new Mock<IBenefitPlanService>();
        planService.Setup(s => s.GetPlanAsync("id-1", Tenant)).ReturnsAsync(plan);

        var adapter = new ChoBenefitPlanAdapter(
            planService.Object,
            Mock.Of<IBenefitViewService>(),
            NullLogger<ChoBenefitPlanAdapter>.Instance);

        var response = await adapter.GetPlanAsync(new BenefitPlanAdapterRequest
        {
            TenantId = Tenant,
            PlanId = "id-1",
        });

        response.Platform.Should().Be("cho");
        response.Plan.Should().NotBeNull();
        response.Plan!.PlanId.Should().Be("plan-x");
        response.Plan.PlanName.Should().Be("Plan X");
    }

    [Fact]
    public async Task GetPlanAsync_returns_null_payload_when_not_found()
    {
        var planService = new Mock<IBenefitPlanService>();
        planService.Setup(s => s.GetPlanAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((BenefitPlan?)null);

        var adapter = new ChoBenefitPlanAdapter(
            planService.Object,
            Mock.Of<IBenefitViewService>(),
            NullLogger<ChoBenefitPlanAdapter>.Instance);

        var response = await adapter.GetPlanAsync(new BenefitPlanAdapterRequest
        {
            TenantId = Tenant,
            PlanId = "missing",
        });

        response.Plan.Should().BeNull();
    }

    [Fact]
    public async Task GetPlanVersionAsync_delegates_to_GetVersionAsync()
    {
        var plan = new BenefitPlan
        {
            Id = "id-2",
            TenantId = Tenant,
            PlanId = "plan-y",
            VersionId = "01HV-VERSION-XYZ",
            VersionNumber = 3,
        };
        var planService = new Mock<IBenefitPlanService>();
        planService.Setup(s => s.GetVersionAsync("plan-y", "01HV-VERSION-XYZ", Tenant))
            .ReturnsAsync(plan);

        var adapter = new ChoBenefitPlanAdapter(
            planService.Object,
            Mock.Of<IBenefitViewService>(),
            NullLogger<ChoBenefitPlanAdapter>.Instance);

        var response = await adapter.GetPlanVersionAsync(new BenefitPlanAdapterRequest
        {
            TenantId = Tenant,
            PlanId = "plan-y",
            VersionId = "01HV-VERSION-XYZ",
        });

        response.Plan!.VersionId.Should().Be("01HV-VERSION-XYZ");
        response.Plan.VersionNumber.Should().Be(3);
    }

    [Fact]
    public async Task GetPlanVersionAsync_throws_when_version_id_missing()
    {
        var adapter = new ChoBenefitPlanAdapter(
            Mock.Of<IBenefitPlanService>(),
            Mock.Of<IBenefitViewService>(),
            NullLogger<ChoBenefitPlanAdapter>.Instance);

        var request = new BenefitPlanAdapterRequest { TenantId = Tenant, PlanId = "p" };
        await adapter.Invoking(a => a.GetPlanVersionAsync(request))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetMemberBenefitViewAsync_delegates_to_view_service_and_uses_service_date()
    {
        var view = new MemberBenefitView
        {
            PlanId = "plan-z",
            PlanName = "Plan Z",
            Payer = "Acme",
            PlanType = "PPO",
            EffectiveDate = new DateTime(2026, 1, 1),
            AsOfDate = new DateTime(2026, 4, 1),
            PlanVersion = "20260401T000000Z",
        };
        var viewService = new Mock<IBenefitViewService>();
        viewService.Setup(v => v.GetMemberViewAsync("plan-z", Tenant, new DateTime(2026, 4, 1)))
            .ReturnsAsync(view);

        var adapter = new ChoBenefitPlanAdapter(
            Mock.Of<IBenefitPlanService>(),
            viewService.Object,
            NullLogger<ChoBenefitPlanAdapter>.Instance);

        var response = await adapter.GetMemberBenefitViewAsync(new BenefitPlanAdapterRequest
        {
            TenantId = Tenant,
            PlanId = "plan-z",
            ServiceDate = new DateTime(2026, 4, 1),
        });

        response.Platform.Should().Be("cho");
        response.View.Should().NotBeNull();
        response.View!.PlanId.Should().Be("plan-z");
        response.View.PlanVersion.Should().Be("20260401T000000Z");
    }
}
