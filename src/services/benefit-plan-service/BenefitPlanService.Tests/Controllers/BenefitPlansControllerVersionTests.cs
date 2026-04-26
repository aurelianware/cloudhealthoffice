using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

public class BenefitPlansControllerVersionTests
{
    private const string Tenant = "tenant-a";

    private static (BenefitPlansController controller,
                    BenefitPlanServiceImpl service,
                    InMemoryBenefitPlanRepository repo) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var transitions = new InMemoryPlanVersionTransitionRepository();
        var events = new FakePlanVersionEventPublisher();
        var service = new BenefitPlanServiceImpl(repo, transitions, events, NullLogger<BenefitPlanServiceImpl>.Instance);
        var controller = new BenefitPlansController(service, NullLogger<BenefitPlansController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.ControllerContext.HttpContext.Items["TenantId"] = Tenant;
        return (controller, service, repo);
    }

    private static BenefitPlan SamplePlan(string planId = "plan-x") => new()
    {
        TenantId = Tenant,
        PlanId = planId,
        PlanName = "Plan X",
        Payer = "Acme",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
    };

    [Fact]
    public async Task GetVersions_returns_paginated_history()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");
        var draft2 = await service.AmendPublishedPlanAsync(v1.PlanId, Tenant, "user");
        var v2 = await service.PublishVersionAsync(draft2.PlanId, draft2.VersionId, Tenant, "user");

        var result = await controller.GetVersions(v1.PlanId);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var page = ok.Value.Should().BeOfType<PlanVersionPage>().Subject;
        page.Items.Should().HaveCount(2);
        page.Items[0].VersionId.Should().Be(v2.VersionId);
    }

    [Fact]
    public async Task GetVersion_returns_404_for_unknown_version()
    {
        var (controller, _, _) = Build();
        var result = await controller.GetVersion("unknown", "nope");
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UpdatePlan_against_published_returns_409_with_explanation()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        v1.PlanName = "New name without amendment";
        var result = await controller.UpdatePlan(v1.Id, v1);
        var conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().NotBeNull();
        conflict.Value!.GetType().GetProperty("versionState")!.GetValue(conflict.Value)!.ToString().Should().Be("Published");
    }

    [Fact]
    public async Task CreateDraft_returns_201_for_new_plan()
    {
        var (controller, _, _) = Build();
        var result = await controller.CreateDraft(SamplePlan());
        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Publish_publishes_draft_returns_200()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");

        var result = await controller.Publish(draft.PlanId, draft.VersionId);
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Publish_unknown_version_returns_404()
    {
        var (controller, _, _) = Build();
        var result = await controller.Publish("plan-x", "no-such-version");
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Amend_with_no_published_version_returns_404()
    {
        var (controller, _, _) = Build();
        var result = await controller.Amend("plan-x");
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Supersede_returns_409_today()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.Supersede(v1.PlanId, v1.VersionId,
            new SupersedeRequest { Reason = "test" });
        result.Result.Should().BeOfType<ConflictObjectResult>();
    }
}
