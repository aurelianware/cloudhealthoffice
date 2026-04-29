using System.Text.Json.Nodes;
using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// Capability BP 5.9 — FHIR Endpoint endpoint behavior, search parameter
/// semantics, tenant scoping, FHIR OperationOutcome on errors. Mirrors
/// <see cref="FhirInsurancePlanControllerTests"/>.
/// </summary>
public sealed class FhirEndpointControllerTests
{
    private const string Tenant = "tenant-a";

    [Fact]
    public async Task ReadEndpoint_returns_404_for_missing_id()
    {
        var (controller, _) = Build();

        var result = await controller.ReadEndpoint("does-not-exist", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("OperationOutcome");
        content.Content.Should().Contain("not-found");
    }

    [Fact]
    public async Task ReadEndpoint_returns_400_for_blank_id()
    {
        var (controller, _) = Build();

        var result = await controller.ReadEndpoint(" ", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("invalid");
    }

    [Fact]
    public async Task ReadEndpoint_returns_projected_endpoint_for_published_plan_document()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "GOLD-2026");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-sbc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
            ContentType = "application/pdf",
        });
        await repo.CreateAsync(plan);

        var result = await controller.ReadEndpoint("doc-sbc", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);

        var json = JsonNode.Parse(content.Content!)!.AsObject();
        json["resourceType"]!.GetValue<string>().Should().Be("Endpoint");
        json["id"]!.GetValue<string>().Should().Be("doc-sbc");
        json["status"]!.GetValue<string>().Should().Be("active");
        json["address"]!.GetValue<string>().Should().Be("https://example.com/sbc.pdf");
    }

    [Fact]
    public async Task ReadEndpoint_scopes_by_tenant()
    {
        var (controller, repo) = Build();
        var otherTenant = SamplePlan(planId: "ISOLATED");
        otherTenant.TenantId = "tenant-b";
        otherTenant.Documents.Add(new PlanDocumentReference
        {
            Id = "isolated-doc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        await repo.CreateAsync(otherTenant);

        var result = await controller.ReadEndpoint("isolated-doc", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404,
            "tenant-a must not see tenant-b's endpoints even when ids match");
    }

    [Fact]
    public async Task ReadEndpoint_returns_404_when_plan_is_not_published()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "DRAFT-2026");
        plan.VersionState = PlanVersionState.Draft;
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "draft-doc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        await repo.CreateAsync(plan);

        var result = await controller.ReadEndpoint("draft-doc", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404,
            "non-published plans don't surface their endpoints");
    }

    [Fact]
    public async Task SearchEndpoints_returns_searchset_bundle()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-sbc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-form",
            DocType = PlanDocumentType.Formulary,
            Location = "https://example.com/formulary.pdf",
        });
        await repo.CreateAsync(plan);

        var result = await controller.SearchEndpoints(
            _id: null, status: null, connectionType: null,
            _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();

        bundle["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        bundle["type"]!.GetValue<string>().Should().Be("searchset");
        bundle["total"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task SearchEndpoints_by_id_returns_single_match()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-sbc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-form",
            DocType = PlanDocumentType.Formulary,
            Location = "https://example.com/formulary.pdf",
        });
        await repo.CreateAsync(plan);

        var result = await controller.SearchEndpoints(
            _id: "doc-sbc", status: null, connectionType: null,
            _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(1);
        bundle["entry"]!.AsArray()[0]!["resource"]!["id"]!.GetValue<string>()
            .Should().Be("doc-sbc");
    }

    [Fact]
    public async Task SearchEndpoints_by_status_filters_correctly()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-active",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/a.pdf",
        });
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-future",
            DocType = PlanDocumentType.EOC,
            Location = "https://example.com/b.pdf",
            EffectiveDate = DateTime.UtcNow.AddDays(30),
        });
        await repo.CreateAsync(plan);

        var activeOnly = await controller.SearchEndpoints(
            _id: null, status: "active", connectionType: null,
            _count: 50, _page: 1, default);
        var activeBundle = JsonNode.Parse((activeOnly as ContentResult)!.Content!)!.AsObject();
        activeBundle["total"]!.GetValue<int>().Should().Be(1);

        var offOnly = await controller.SearchEndpoints(
            _id: null, status: "off", connectionType: null,
            _count: 50, _page: 1, default);
        var offBundle = JsonNode.Parse((offOnly as ContentResult)!.Content!)!.AsObject();
        offBundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task SearchEndpoints_by_connection_type_static_document_returns_matches()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-sbc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        await repo.CreateAsync(plan);

        var matching = await controller.SearchEndpoints(
            _id: null, status: null,
            connectionType: ChoBenefitPlanFhirUrls.EndpointConnectionTypeStaticDocument,
            _count: 50, _page: 1, default);
        var bundle = JsonNode.Parse((matching as ContentResult)!.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task SearchEndpoints_by_unknown_connection_type_returns_empty()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-sbc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        await repo.CreateAsync(plan);

        var result = await controller.SearchEndpoints(
            _id: null, status: null, connectionType: "hl7-fhir-rest",
            _count: 50, _page: 1, default);
        var bundle = JsonNode.Parse((result as ContentResult)!.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task SearchEndpoints_skips_internal_reference_documents()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-internal",
            DocType = PlanDocumentType.SBC,
            Location = "documentreference/abc-123",
        });
        await repo.CreateAsync(plan);

        var result = await controller.SearchEndpoints(
            _id: null, status: null, connectionType: null,
            _count: 50, _page: 1, default);
        var bundle = JsonNode.Parse((result as ContentResult)!.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(0,
            "internal documentreference/{id} entries are skipped per Decision 4");
    }

    [Fact]
    public async Task ReadEndpoint_emits_application_fhir_json_content_type()
    {
        var (controller, repo) = Build();
        var plan = SamplePlan(planId: "PLAN-1");
        plan.Documents.Add(new PlanDocumentReference
        {
            Id = "doc-sbc",
            DocType = PlanDocumentType.SBC,
            Location = "https://example.com/sbc.pdf",
        });
        await repo.CreateAsync(plan);

        var result = await controller.ReadEndpoint("doc-sbc", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;

        content.ContentType.Should().StartWith("application/fhir+json");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static (FhirEndpointController controller, InMemoryBenefitPlanRepository repo) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var controller = new FhirEndpointController(
            repo,
            new FhirEndpointProjector(),
            NullLogger<FhirEndpointController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, repo);
    }

    private static BenefitPlan SamplePlan(string planId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        PlanId = planId,
        PlanName = planId,
        Payer = "AurelianHealth",
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        VersionState = PlanVersionState.Published,
        VersionNumber = 1,
        VersionId = Guid.NewGuid().ToString(),
        PublishedAt = DateTime.UtcNow,
        Documents = new List<PlanDocumentReference>(),
    };
}
