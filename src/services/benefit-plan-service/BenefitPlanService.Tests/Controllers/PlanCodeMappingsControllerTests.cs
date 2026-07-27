using BenefitPlanService.Controllers;
using BenefitPlanService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

public sealed class PlanCodeMappingsControllerTests
{
    private static PlanCodeMappingsController Build(
        InMemoryEnrollment834PlanCodeMappingRepository repo, string? tenantId = "tenant-a")
    {
        var controller = new PlanCodeMappingsController(repo, NullLogger<PlanCodeMappingsController>.Instance);
        var httpContext = new DefaultHttpContext();
        if (tenantId is not null)
        {
            httpContext.Items["TenantId"] = tenantId;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public async Task Resolve_MissingTenant_ReturnsBadRequest()
    {
        var controller = Build(new InMemoryEnrollment834PlanCodeMappingRepository(), tenantId: null);

        var result = await controller.Resolve("GRP0001", "HLT", "PPO2026", default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Resolve_NoMapping_ReturnsNotFound()
    {
        var controller = Build(new InMemoryEnrollment834PlanCodeMappingRepository());

        var result = await controller.Resolve("GRP0001", "HLT", "PPO2026", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Create_ThenResolve_RoundTrips()
    {
        var repo = new InMemoryEnrollment834PlanCodeMappingRepository();
        var controller = Build(repo);

        var created = await controller.Create(new CreatePlanCodeMappingRequest
        {
            GroupNumber = "GRP0001",
            InsuranceLineCode = "HLT",
            ExternalPlanCode = "PPO2026",
            PlanId = "plan-guid-123"
        }, default);
        created.Should().BeOfType<CreatedAtActionResult>();

        var resolved = await controller.Resolve("GRP0001", "HLT", "PPO2026", default);
        var ok = resolved.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<PlanCodeMappingResponse>().Subject;
        response.PlanId.Should().Be("plan-guid-123");
    }

    [Fact]
    public async Task Resolve_DoesNotCrossTenants()
    {
        var repo = new InMemoryEnrollment834PlanCodeMappingRepository();
        var tenantAController = Build(repo, tenantId: "tenant-a");
        await tenantAController.Create(new CreatePlanCodeMappingRequest
        {
            GroupNumber = "GRP0001",
            InsuranceLineCode = "HLT",
            ExternalPlanCode = "PPO2026",
            PlanId = "plan-guid-123"
        }, default);

        var tenantBController = Build(repo, tenantId: "tenant-b");
        var result = await tenantBController.Resolve("GRP0001", "HLT", "PPO2026", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task List_FiltersByGroupNumber()
    {
        var repo = new InMemoryEnrollment834PlanCodeMappingRepository();
        var controller = Build(repo);
        await controller.Create(new CreatePlanCodeMappingRequest
        {
            GroupNumber = "GRP0001", InsuranceLineCode = "HLT", ExternalPlanCode = "A", PlanId = "p1"
        }, default);
        await controller.Create(new CreatePlanCodeMappingRequest
        {
            GroupNumber = "GRP0002", InsuranceLineCode = "HLT", ExternalPlanCode = "B", PlanId = "p2"
        }, default);

        var result = await controller.List("GRP0001", default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value.Should().BeAssignableTo<List<PlanCodeMappingResponse>>().Subject;
        items.Should().ContainSingle();
        items[0].PlanId.Should().Be("p1");
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsNotFound()
    {
        var controller = Build(new InMemoryEnrollment834PlanCodeMappingRepository());

        var result = await controller.Delete(Guid.NewGuid().ToString(), default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Create_Duplicate_ReturnsConflict()
    {
        var repo = new InMemoryEnrollment834PlanCodeMappingRepository();
        var controller = Build(repo);
        var request = new CreatePlanCodeMappingRequest
        {
            GroupNumber = "GRP0001", InsuranceLineCode = "HLT", ExternalPlanCode = "PPO2026", PlanId = "p1"
        };
        await controller.Create(request, default);

        var result = await controller.Create(request, default);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateBulk_EmptyList_ReturnsBadRequest()
    {
        var controller = Build(new InMemoryEnrollment834PlanCodeMappingRepository());

        var result = await controller.CreateBulk(new List<CreatePlanCodeMappingRequest>(), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateBulk_MixOfValidAndDuplicateAndIncomplete_PartiallySucceeds()
    {
        var repo = new InMemoryEnrollment834PlanCodeMappingRepository();
        var controller = Build(repo);
        // Pre-seed one row so the bulk request's second row collides with it.
        await controller.Create(new CreatePlanCodeMappingRequest
        {
            GroupNumber = "GRP0001", InsuranceLineCode = "HLT", ExternalPlanCode = "DUP", PlanId = "existing"
        }, default);

        var result = await controller.CreateBulk(new List<CreatePlanCodeMappingRequest>
        {
            new() { GroupNumber = "GRP0001", InsuranceLineCode = "HLT", ExternalPlanCode = "PPO2026", PlanId = "p1" },
            new() { GroupNumber = "GRP0001", InsuranceLineCode = "HLT", ExternalPlanCode = "DUP", PlanId = "p2" },
            new() { GroupNumber = "GRP0001", InsuranceLineCode = "HLT", ExternalPlanCode = "", PlanId = "p3" },
        }, default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<BulkPlanCodeMappingResult>().Subject;
        body.Created.Should().ContainSingle(c => c.ExternalPlanCode == "PPO2026");
        body.Errors.Should().HaveCount(2);
        body.Errors.Should().ContainSingle(e => e.Index == 1 && e.Error.Contains("already exists"));
        body.Errors.Should().ContainSingle(e => e.Index == 2 && e.Error.Contains("required"));
    }
}
