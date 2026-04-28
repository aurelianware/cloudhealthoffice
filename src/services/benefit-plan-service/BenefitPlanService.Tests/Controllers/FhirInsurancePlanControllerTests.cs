using System.Text.Json;
using System.Text.Json.Nodes;
using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Repositories;
using BenefitPlanService.Services;
using BenefitPlanService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// Capability BP 5.8 — FHIR InsurancePlan endpoint behavior, search
/// parameter semantics, tenant scoping, FHIR OperationOutcome on errors.
/// Mirrors the shape of <c>FhirPractitionerControllerTests</c> in
/// provider-service. Uses an in-memory repository fake so the
/// controller can exercise the full read / search path without Cosmos
/// or Mongo.
/// </summary>
public sealed class FhirInsurancePlanControllerTests
{
    private const string Tenant = "tenant-a";

    [Fact]
    public async Task ReadInsurancePlan_returns_404_for_missing_plan()
    {
        var (controller, _) = Build();

        var result = await controller.ReadInsurancePlan("does-not-exist", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404);
        content.ContentType.Should().StartWith("application/fhir+json");
        content.Content.Should().Contain("OperationOutcome");
        content.Content.Should().Contain("not-found");
    }

    [Fact]
    public async Task ReadInsurancePlan_returns_400_for_blank_id()
    {
        var (controller, _) = Build();

        var result = await controller.ReadInsurancePlan(" ", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
        content.Content.Should().Contain("invalid");
    }

    [Fact]
    public async Task ReadInsurancePlan_returns_projected_fhir_for_active_plan()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "GOLD-2026"));

        var result = await controller.ReadInsurancePlan("GOLD-2026", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);

        var json = JsonNode.Parse(content.Content!)!.AsObject();
        json["resourceType"]!.GetValue<string>().Should().Be("InsurancePlan");
        json["id"]!.GetValue<string>().Should().Be("GOLD-2026");
        json["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public async Task ReadInsurancePlan_scopes_by_tenant()
    {
        var (controller, repo) = Build();
        var otherTenant = SamplePlan(planId: "ISOLATED");
        otherTenant.TenantId = "tenant-b";
        await repo.CreateAsync(otherTenant);

        var result = await controller.ReadInsurancePlan("ISOLATED", default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(404,
            "tenant-a must not see tenant-b's plan even when the PlanId matches");
    }

    [Fact]
    public async Task SearchInsurancePlans_returns_searchset_bundle()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "PLAN-1", name: "Aurelian Gold"));
        await repo.CreateAsync(SamplePlan(planId: "PLAN-2", name: "Aurelian Silver"));

        var result = await controller.SearchInsurancePlans(
            identifier: null, name: null, status: null, _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();

        bundle["resourceType"]!.GetValue<string>().Should().Be("Bundle");
        bundle["type"]!.GetValue<string>().Should().Be("searchset");
        bundle["total"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task SearchInsurancePlans_by_identifier_returns_single_match()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "PLAN-1"));
        await repo.CreateAsync(SamplePlan(planId: "PLAN-2"));

        var result = await controller.SearchInsurancePlans(
            identifier: "PLAN-1", name: null, status: null, _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(1);
        bundle["entry"]!.AsArray()[0]!["resource"]!["id"]!.GetValue<string>()
            .Should().Be("PLAN-1");
    }

    [Fact]
    public async Task SearchInsurancePlans_by_identifier_with_system_pipe()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "PLAN-1"));

        var qualified = $"{ChoBenefitPlanFhirUrls.PlanIdSystem}|PLAN-1";
        var result = await controller.SearchInsurancePlans(
            identifier: qualified, name: null, status: null, _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task SearchInsurancePlans_by_unknown_identifier_system_returns_empty()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "PLAN-1"));

        var result = await controller.SearchInsurancePlans(
            identifier: "http://other-system|PLAN-1",
            name: null, status: null, _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task SearchInsurancePlans_filters_by_name_substring()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "PLAN-1", name: "Aurelian Gold"));
        await repo.CreateAsync(SamplePlan(planId: "PLAN-2", name: "Aurelian Silver"));
        await repo.CreateAsync(SamplePlan(planId: "PLAN-3", name: "Helix Bronze"));

        var result = await controller.SearchInsurancePlans(
            identifier: null, name: "Aurelian", status: null, _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        bundle["total"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task SearchInsurancePlans_status_active_filters_terminated_plans()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "ACTIVE", name: "Active"));
        var retired = SamplePlan(planId: "RETIRED", name: "Retired");
        retired.EffectiveDate = DateTime.UtcNow.AddYears(-2);
        retired.TerminationDate = DateTime.UtcNow.AddDays(-30);
        await repo.CreateAsync(retired);

        var activeOnly = await controller.SearchInsurancePlans(
            identifier: null, name: null, status: "active", _count: 50, _page: 1, default);
        var activeBundle = JsonNode.Parse((activeOnly as ContentResult)!.Content!)!.AsObject();
        activeBundle["total"]!.GetValue<int>().Should().Be(1);
        activeBundle["entry"]!.AsArray()[0]!["resource"]!["id"]!.GetValue<string>()
            .Should().Be("ACTIVE");

        var retiredOnly = await controller.SearchInsurancePlans(
            identifier: null, name: null, status: "retired", _count: 50, _page: 1, default);
        var retiredBundle = JsonNode.Parse((retiredOnly as ContentResult)!.Content!)!.AsObject();
        retiredBundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task SearchInsurancePlans_dedupes_to_head_published_per_PlanId()
    {
        var (controller, repo) = Build();

        // v1 published, v2 published (the head).
        var v1 = SamplePlan(planId: "PLAN-DUPE", name: "Dupe v1");
        v1.VersionNumber = 1;
        await repo.CreateAsync(v1);

        var v2 = SamplePlan(planId: "PLAN-DUPE", name: "Dupe v2");
        v2.VersionNumber = 2;
        await repo.CreateAsync(v2);

        // A draft and a superseded row that must NOT surface in search.
        var draft = SamplePlan(planId: "PLAN-DRAFT", name: "Draft");
        draft.VersionState = PlanVersionState.Draft;
        await repo.CreateAsync(draft);

        var superseded = SamplePlan(planId: "PLAN-OLD", name: "Old");
        superseded.VersionState = PlanVersionState.Superseded;
        await repo.CreateAsync(superseded);

        var result = await controller.SearchInsurancePlans(
            identifier: null, name: null, status: null, _count: 50, _page: 1, default);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        var entries = bundle["entry"]!.AsArray();

        entries.Should().HaveCount(1, "draft + superseded must be filtered, dupe collapses to head");
        entries[0]!["resource"]!["id"]!.GetValue<string>().Should().Be("PLAN-DUPE");
        // Head version v2 wins over v1 even though both are Published.
        entries[0]!["resource"]!["name"]!.GetValue<string>().Should().Be("Dupe v2");
    }

    [Fact]
    public async Task ReadInsurancePlan_emits_application_fhir_json_content_type()
    {
        var (controller, repo) = Build();
        await repo.CreateAsync(SamplePlan(planId: "PLAN-1"));

        var result = await controller.ReadInsurancePlan("PLAN-1", default);
        var content = result.Should().BeOfType<ContentResult>().Subject;

        content.ContentType.Should().StartWith("application/fhir+json");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static (FhirInsurancePlanController controller, InMemoryBenefitPlanRepository repo) Build()
    {
        var repo = new InMemoryBenefitPlanRepository();
        var controller = new FhirInsurancePlanController(
            repo,
            new FhirInsurancePlanProjector(),
            new StubOrganizationLookup(),
            new StubAcaLimits(),
            new PlanYearResolver(),
            NullLogger<FhirInsurancePlanController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, repo);
    }

    private static BenefitPlan SamplePlan(string planId, string? name = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        PlanId = planId,
        PlanName = name ?? planId,
        Payer = "AurelianHealth",
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        VersionState = PlanVersionState.Published,
        VersionNumber = 1,
        VersionId = Guid.NewGuid().ToString(),
        PublishedAt = DateTime.UtcNow,
        FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
        Benefits = new List<Benefit>
        {
            new MedicalBenefit { ServiceCategory = "Office Visit", InNetworkCopay = 25m },
        },
        NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        },
        CostSharing = new CostSharing
        {
            IndividualDeductible = 1_000m,
            FamilyDeductible = 2_000m,
            IndividualOutOfPocketMax = 5_000m,
            FamilyOutOfPocketMax = 10_000m,
        },
    };

    private sealed class StubOrganizationLookup : IOrganizationLookupClient
    {
        public Task<OrganizationLookupResult?> GetOrganizationAsync(
            string networkId, CancellationToken ct = default)
            => Task.FromResult<OrganizationLookupResult?>(
                new OrganizationLookupResult
                {
                    OrganizationId = networkId,
                    Name = $"Network {networkId}",
                    EffectiveDate = DateTime.UtcNow.AddYears(-1),
                });
    }

    private sealed class StubAcaLimits : IAcaLimitsProvider
    {
        public AcaLimits? GetForPlanYear(int planYear)
            => new(planYear, 10_600m, 21_200m);

        public IReadOnlyCollection<int> ConfiguredPlanYears => new[] { 2026 };
    }
}
