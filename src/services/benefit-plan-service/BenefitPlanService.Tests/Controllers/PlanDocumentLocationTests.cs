using BenefitPlanService.Adapters;
using BenefitPlanService.Controllers;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Adapters;
using BenefitPlanService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// Plan document locations are rendered as links by the portal. Writes
/// accept only https URLs on BenefitPlan:AllowedDocumentHosts (or the
/// internal documentreference/{id} form); reads withhold any stored value
/// that fails the same rule.
/// </summary>
public class PlanDocumentLocationTests
{
    private const string Tenant = "tenant-a";
    private const string AllowedHost = "docs.payer.example";

    private static readonly PlanDocumentLocationPolicy Policy = new(new[] { AllowedHost });

    private sealed record Harness(
        BenefitPlansController Plans,
        BenefitPlanMemberViewController MemberView,
        BenefitPlanServiceImpl Service,
        InMemoryBenefitPlanRepository Repo);

    private static Harness Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var service = new BenefitPlanServiceImpl(
            repo,
            new InMemoryPlanVersionTransitionRepository(),
            new FakePlanVersionEventPublisher(),
            new NoOpNetworkTierSoftValidator(),
            new NoOpPlanLimitValidator(),
            NullLogger<BenefitPlanServiceImpl>.Instance);
        var viewService = new BenefitViewService(service, NullLogger<BenefitViewService>.Instance);
        var adapter = new ChoBenefitPlanAdapter(service, viewService, NullLogger<ChoBenefitPlanAdapter>.Instance);
        var cache = new BenefitPlanTenantConfigCache(
            new StubHttpClientFactory(FakeHttpMessageHandler.Status(System.Net.HttpStatusCode.NotFound)),
            new ConfigurationBuilder().Build(),
            NullLogger<BenefitPlanTenantConfigCache>.Instance);
        var factory = new BenefitPlanAdapterFactory(
            new[] { adapter }, cache, NullLogger<BenefitPlanAdapterFactory>.Instance);

        var plans = new BenefitPlansController(
            service, factory, new FakeCurrentActor("token-user"),
            NullLogger<BenefitPlansController>.Instance, Policy)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        plans.ControllerContext.HttpContext.Items["TenantId"] = Tenant;

        var memberView = new BenefitPlanMemberViewController(
            factory, NullLogger<BenefitPlanMemberViewController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        memberView.ControllerContext.HttpContext.Items["TenantId"] = Tenant;

        return new Harness(plans, memberView, service, repo);
    }

    private static BenefitPlan PlanWithDocument(string location, string planId = "plan-docs") => new()
    {
        TenantId = Tenant,
        PlanId = planId,
        PlanName = "Plan Docs",
        Payer = "Acme",
        EffectiveDate = new DateTime(2026, 1, 1),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        Documents = new List<PlanDocumentReference>
        {
            new() { Id = "doc-1", DocType = PlanDocumentType.SBC, Location = location, DisplayName = "SBC" },
        },
    };

    public static TheoryData<string> RejectedLocations => new()
    {
        "javascript:alert(document.cookie)",
        "JavaScript:alert(1)",
        "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
        "file:///etc/passwd",
        "http://docs.payer.example/sbc.pdf",
        "https://user:secret@docs.payer.example/sbc.pdf",
        "https://docs.payer.example@evil.example/sbc.pdf",
        "https://127.0.0.1/sbc.pdf",
        "https://10.0.0.12/sbc.pdf",
        "https://192.168.1.10/sbc.pdf",
        "https://172.16.0.1/sbc.pdf",
        "https://169.254.169.254/latest/meta-data/",
        "https://[::1]/sbc.pdf",
        "https://2130706433/sbc.pdf",
        "https://localhost/sbc.pdf",
        "https://member-service.cloudhealthoffice/sbc.pdf",
        "https://member-service.cloudhealthoffice.svc.cluster.local/sbc.pdf",
        "https://member-service.svc/sbc.pdf",
        "https://member-service/sbc.pdf",
        "https://evil.example/sbc.pdf",
        "https://docs.payer.example.evil.example/sbc.pdf",
        "https://docs.payer.example:8443/sbc.pdf",
        "//docs.payer.example/sbc.pdf",
        "/relative/sbc.pdf",
        "documentreference/../admin",
        "https://docs.payer.example\\@evil.example/sbc.pdf",
    };

    [Theory]
    [MemberData(nameof(RejectedLocations))]
    public async Task CreateDraft_rejects_disallowed_location_with_400(string location)
    {
        var h = Build();

        var result = await h.Plans.CreateDraft(PlanWithDocument(location));

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("documents[0].location");
        (await h.Repo.SearchAsync(Tenant, null, null, null, 1, 50)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(RejectedLocations))]
    public async Task CreatePlan_rejects_disallowed_location_with_400(string location)
    {
        var h = Build();

        var result = await h.Plans.CreatePlan(PlanWithDocument(location));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdatePlan_rejects_javascript_location_with_400()
    {
        var h = Build();
        var plan = PlanWithDocument("javascript:alert(1)");

        var result = await h.Plans.UpdatePlan(plan.Id, plan);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("https://docs.payer.example/sbc-2026.pdf")]
    [InlineData("HTTPS://DOCS.PAYER.EXAMPLE/sbc-2026.pdf")]
    [InlineData("documentreference/abc-123")]
    public async Task CreateDraft_accepts_allowed_host_or_internal_reference(string location)
    {
        var h = Build();

        var result = await h.Plans.CreateDraft(PlanWithDocument(location));

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Without_configured_hosts_any_external_url_is_rejected()
    {
        var h = Build();
        var controller = new BenefitPlansController(
            h.Service, null!, new FakeCurrentActor("token-user"),
            NullLogger<BenefitPlansController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.ControllerContext.HttpContext.Items["TenantId"] = Tenant;

        var result = await controller.CreateDraft(PlanWithDocument("https://docs.payer.example/sbc.pdf"));

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── read side: values stored before the rule existed ───────────────

    private static async Task<object?> RunResultFilter(ControllerBase controller, IActionResult result)
    {
        var filter = new PlanDocumentLocationResultFilter(Policy);
        var ctx = new ResultExecutingContext(
            new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), result, controller);
        await filter.OnResultExecutionAsync(ctx, () =>
            Task.FromResult(new ResultExecutedContext(ctx, ctx.Filters, ctx.Result, controller)));
        return ((ObjectResult)ctx.Result).Value;
    }

    [Fact]
    public async Task Stored_bad_location_is_returned_null_and_flagged_on_plan_read()
    {
        var h = Build();
        // Service-level write bypasses the controller: simulates data
        // persisted before validation existed.
        var draft = await h.Service.CreateDraftAsync(
            PlanWithDocument("javascript:alert(1)"), Tenant, "seed");

        var result = await h.Plans.GetVersion(draft.PlanId, draft.VersionId);
        var value = await RunResultFilter(h.Plans, result.Result!);

        var doc = value.Should().BeOfType<BenefitPlan>().Subject.Documents.Single();
        doc.Location.Should().BeNull();
        doc.LocationBlocked.Should().BeTrue();
        doc.DisplayName.Should().Be("SBC");

        // The stored row is untouched; only the response is redacted.
        var stored = (await h.Repo.SearchAsync(Tenant, null, null, null, 1, 50)).Single();
        stored.Documents.Single().Location.Should().Be("javascript:alert(1)");
    }

    [Fact]
    public async Task Stored_bad_location_is_returned_null_on_member_view_read()
    {
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(
            PlanWithDocument("https://member-service.cloudhealthoffice.svc.cluster.local/x"), Tenant, "seed");
        await h.Service.PublishVersionAsync(draft.PlanId, draft.VersionId, Tenant, "seed");

        var result = await h.MemberView.GetMemberView(draft.PlanId, new DateTime(2026, 6, 1));
        var value = await RunResultFilter(h.MemberView, result.Result!);

        var doc = value.Should().BeOfType<MemberBenefitView>().Subject.Documents.Single();
        doc.Location.Should().BeNull();
        doc.LocationBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Allowed_stored_location_is_returned_unchanged()
    {
        var h = Build();
        var draft = await h.Service.CreateDraftAsync(
            PlanWithDocument("https://docs.payer.example/sbc.pdf"), Tenant, "seed");

        var result = await h.Plans.GetVersion(draft.PlanId, draft.VersionId);
        var value = await RunResultFilter(h.Plans, result.Result!);

        var doc = ((BenefitPlan)value!).Documents.Single();
        doc.Location.Should().Be("https://docs.payer.example/sbc.pdf");
        doc.LocationBlocked.Should().BeFalse();
    }

    [Fact]
    public void Plan_list_reads_are_sanitized()
    {
        var filter = new PlanDocumentLocationResultFilter(Policy);
        IEnumerable<BenefitPlan> lazy = new[] { PlanWithDocument("data:text/html,x") }.Select(p => p);

        var value = filter.Sanitize(lazy);

        value.Should().BeAssignableTo<IEnumerable<BenefitPlan>>()
            .Subject.Single().Documents.Single().Location.Should().BeNull();
    }

    [Fact]
    public void Fhir_endpoint_is_not_projected_for_stored_bad_location()
    {
        var projector = new FhirEndpointProjector(Policy);
        var plan = PlanWithDocument("https://169.254.169.254/latest/meta-data/");
        plan.VersionState = PlanVersionState.Published;
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-ok", DocType = PlanDocumentType.EOC, Location = "https://docs.payer.example/eoc.pdf",
        });

        projector.OrderedProjectableDocuments(plan).Select(d => d.Id).Should().Equal("doc-ok");
        projector.Project(plan, plan.Documents[0]).Should().BeNull();
        projector.Project(plan, plan.Documents[1]).Should().NotBeNull();
    }
}
