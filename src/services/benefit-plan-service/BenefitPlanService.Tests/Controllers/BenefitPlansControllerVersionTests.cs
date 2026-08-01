using BenefitPlanService.Adapters;
using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Adapters;
using BenefitPlanService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

public class BenefitPlansControllerVersionTests
{
    private const string Tenant = "tenant-a";

    private static (BenefitPlansController controller,
                    BenefitPlanServiceImpl service,
                    InMemoryBenefitPlanRepository repo) Build(
        Func<IBenefitPlanService, IBenefitViewService, IBenefitPlanAdapter>? adapterFactory = null)
    {
        var repo = new InMemoryBenefitPlanRepository();
        var transitions = new InMemoryPlanVersionTransitionRepository();
        var events = new FakePlanVersionEventPublisher();
        var service = new BenefitPlanServiceImpl(repo, transitions, events, new NoOpNetworkTierSoftValidator(), new NoOpPlanLimitValidator(), NullLogger<BenefitPlanServiceImpl>.Instance);
        var viewService = new BenefitViewService(service, NullLogger<BenefitViewService>.Instance);

        // Default adapter: real CHO adapter backed by the in-memory service so
        // controller GET endpoints behave as before this refactor.
        var primary = adapterFactory?.Invoke(service, viewService)
            ?? new ChoBenefitPlanAdapter(service, viewService, NullLogger<ChoBenefitPlanAdapter>.Instance);

        var config = new ConfigurationBuilder().Build();
        var cache = new BenefitPlanTenantConfigCache(
            new StubHttpClientFactory(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.NotFound)),
            config,
            NullLogger<BenefitPlanTenantConfigCache>.Instance);
        var factory = new BenefitPlanAdapterFactory(
            new[] { primary }, cache, NullLogger<BenefitPlanAdapterFactory>.Instance);

        var controller = new BenefitPlansController(service, factory, NullLogger<BenefitPlansController>.Instance)
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
    public async Task Supersede_terminates_published_version_returns_200()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.Supersede(v1.PlanId, v1.VersionId,
            new SupersedeRequest { Reason = "test" });

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var terminated = ok.Value.Should().BeOfType<BenefitPlan>().Subject;
        terminated.VersionState.Should().Be(PlanVersionState.Superseded);
        terminated.SupersededByVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Supersede_draft_version_returns_409()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");

        var result = await controller.Supersede(draft.PlanId, draft.VersionId,
            new SupersedeRequest { Reason = "test" });
        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task DeletePlan_terminates_and_returns_204()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.DeletePlan(v1.PlanId);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeletePlan_unknown_plan_returns_404()
    {
        var (controller, _, _) = Build();
        var result = await controller.DeletePlan("does-not-exist");
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task AddBenefit_amends_and_returns_201()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.AddBenefit(v1.PlanId, new Benefit { ServiceCategory = "Urgent Care", CopayAmount = 50m });
        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task AddBenefit_unknown_plan_returns_404()
    {
        var (controller, _, _) = Build();
        var result = await controller.AddBenefit("does-not-exist", new Benefit { ServiceCategory = "X" });
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UpdateBenefit_amends_and_returns_200()
    {
        var (controller, service, _) = Build();
        var source = SamplePlan();
        source.Benefits.Add(new Benefit { Id = "office", ServiceCategory = "Office Visit", InNetworkCopay = 25m });
        var draft = await service.CreateDraftAsync(source, Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.UpdateBenefit(
            v1.PlanId,
            "office",
            new Benefit { ServiceCategory = "Office Visit", InNetworkCopay = 35m });

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var updated = ok.Value.Should().BeAssignableTo<Benefit>().Subject;
        updated.Id.Should().Be("office");
        updated.InNetworkCopay.Should().Be(35m);
    }

    [Fact]
    public async Task UpdateBenefit_unknown_rule_returns_404()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.UpdateBenefit(
            v1.PlanId,
            "missing",
            new Benefit { ServiceCategory = "X" });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ReplaceNetworkTiers_amends_and_returns_200()
    {
        var (controller, service, _) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.ReplaceNetworkTiers(v1.PlanId, new List<NetworkTier>
        {
            new() { TierName = "Preferred", TierLevel = 1, NetworkId = "NET-A" },
            new() { TierName = "Extended", TierLevel = 2, NetworkId = "NET-B" },
        });

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<NetworkTier>>()
            .Which.Should().HaveCount(2);
        (await service.GetPlanAsync(v1.PlanId, Tenant))!.VersionNumber.Should().Be(2);
    }

    [Fact]
    public async Task ReplaceNetworkTiers_duplicate_level_returns_400()
    {
        var (controller, service, repo) = Build();
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.ReplaceNetworkTiers(v1.PlanId, new List<NetworkTier>
        {
            new() { TierName = "Preferred", TierLevel = 1, NetworkId = "NET-A" },
            new() { TierName = "Extended", TierLevel = 1, NetworkId = "NET-B" },
        });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        repo.Docs.Should().ContainSingle();
    }

    [Fact]
    public async Task ReplaceNetworkTiers_unknown_plan_returns_404()
    {
        var (controller, _, _) = Build();

        var result = await controller.ReplaceNetworkTiers("missing", new List<NetworkTier>());

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetPlan_routes_through_adapter_factory()
    {
        RecordingChoAdapter? recording = null;
        var (controller, service, _) = Build((s, v) =>
            recording = new RecordingChoAdapter(new ChoBenefitPlanAdapter(s, v, NullLogger<ChoBenefitPlanAdapter>.Instance)));
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        // Every real caller passes the business-key PlanId here, not the
        // internal auto-generated Id (see BenefitPlanService.GetPlanAsync's
        // doc comment) -- GetByPlanIdAsync underneath filters on PlanId.
        var result = await controller.GetPlan(v1.PlanId);

        result.Result.Should().BeOfType<OkObjectResult>();
        recording.Should().NotBeNull();
        recording!.GetPlanCalls.Should().HaveCount(1);
        recording.GetPlanCalls[0].PlanId.Should().Be(v1.PlanId);
        recording.GetPlanCalls[0].TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task GetVersion_routes_through_adapter_factory()
    {
        RecordingChoAdapter? recording = null;
        var (controller, service, _) = Build((s, v) =>
            recording = new RecordingChoAdapter(new ChoBenefitPlanAdapter(s, v, NullLogger<ChoBenefitPlanAdapter>.Instance)));
        var draft = await service.CreateDraftAsync(SamplePlan(), Tenant, "user");
        var v1 = await service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "user");

        var result = await controller.GetVersion(v1.PlanId, v1.VersionId);

        result.Result.Should().BeOfType<OkObjectResult>();
        recording.Should().NotBeNull();
        recording!.GetVersionCalls.Should().HaveCount(1);
        recording.GetVersionCalls[0].VersionId.Should().Be(v1.VersionId);
    }

    /// <summary>
    /// Decorator over <see cref="ChoBenefitPlanAdapter"/> that records every
    /// invocation so we can assert the controller hit the factory.
    /// </summary>
    private sealed class RecordingChoAdapter : IBenefitPlanAdapter
    {
        private readonly ChoBenefitPlanAdapter _inner;
        public RecordingChoAdapter(ChoBenefitPlanAdapter inner) { _inner = inner; }

        public string Platform => "cho";
        public List<BenefitPlanAdapterRequest> GetPlanCalls { get; } = new();
        public List<BenefitPlanAdapterRequest> GetVersionCalls { get; } = new();

        public Task<BenefitPlanAdapterResponse> GetPlanAsync(BenefitPlanAdapterRequest request, CancellationToken ct = default)
        {
            GetPlanCalls.Add(Clone(request));
            return _inner.GetPlanAsync(request, ct);
        }

        public Task<BenefitPlanAdapterResponse> GetPlanVersionAsync(BenefitPlanAdapterRequest request, CancellationToken ct = default)
        {
            GetVersionCalls.Add(Clone(request));
            return _inner.GetPlanVersionAsync(request, ct);
        }

        public Task<MemberBenefitViewAdapterResponse> GetMemberBenefitViewAsync(BenefitPlanAdapterRequest request, CancellationToken ct = default)
            => _inner.GetMemberBenefitViewAsync(request, ct);

        private static BenefitPlanAdapterRequest Clone(BenefitPlanAdapterRequest r) => new()
        {
            TenantId = r.TenantId,
            PlanId = r.PlanId,
            VersionId = r.VersionId,
            ServiceDate = r.ServiceDate,
            SubscriberId = r.SubscriberId,
            PlatformSettings = new(r.PlatformSettings),
        };
    }
}
