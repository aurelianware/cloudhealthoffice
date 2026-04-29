using BenefitPlanService.Controllers;
using BenefitPlanService.HostedServices;
using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>
/// Capability BP 5.6 — admin write API for service-category mappings.
/// Verifies the AdminWriteEnabled config gate, request validation, the
/// CRUD lifecycle, tenant isolation, and the seeder integration.
/// </summary>
public sealed class ServiceCategoryMappingsControllerTests
{
    [Fact]
    public async Task List_Returns_Tenant_Defaults_When_PlanId_Omitted()
    {
        var (controller, store, _) = Build(adminEnabled: false, tenantId: "tenant-a");
        await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));
        await store.CreateAsync(Mapping("tenant-a", Guid.NewGuid(), "Inpatient Hospital"));

        var result = await controller.List(planId: null, ct: default);

        var items = (result.Result as OkObjectResult)!.Value as IReadOnlyList<ServiceCategoryMappingResponse>;
        items.Should().HaveCount(1);
        items!.Single().ServiceTypeCode.Should().Be("Office Visit");
    }

    [Fact]
    public async Task List_Returns_Plan_Overrides_When_PlanId_Supplied()
    {
        var planId = Guid.NewGuid();
        var (controller, store, _) = Build(adminEnabled: false, tenantId: "tenant-a");
        await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));
        await store.CreateAsync(Mapping("tenant-a", planId, "Specialist Visit"));

        var result = await controller.List(planId: planId, ct: default);

        var items = (result.Result as OkObjectResult)!.Value as IReadOnlyList<ServiceCategoryMappingResponse>;
        items.Should().HaveCount(1);
        items!.Single().ServiceTypeCode.Should().Be("Specialist Visit");
    }

    [Fact]
    public async Task List_Filters_By_Tenant()
    {
        var (controller, store, _) = Build(adminEnabled: false, tenantId: "tenant-a");
        await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));
        await store.CreateAsync(Mapping("tenant-b", null, "Office Visit"));

        var result = await controller.List(planId: null, ct: default);

        var items = (result.Result as OkObjectResult)!.Value as IReadOnlyList<ServiceCategoryMappingResponse>;
        items.Should().HaveCount(1);
        items!.Single().TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task Create_Returns_503_When_Admin_Write_Disabled()
    {
        var (controller, _, _) = Build(adminEnabled: false, tenantId: "tenant-a");

        var result = await controller.Create(ValidRequest(), default);

        var status = result.Result as ObjectResult;
        status.Should().NotBeNull();
        status!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Create_Persists_Mapping_When_Admin_Write_Enabled()
    {
        var (controller, store, _) = Build(adminEnabled: true, tenantId: "tenant-a");

        var result = await controller.Create(ValidRequest(), default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var created = (result.Result as CreatedAtActionResult)!.Value as ServiceCategoryMappingResponse;
        created.Should().NotBeNull();
        created!.ServiceTypeCode.Should().Be("Office Visit");

        var roundTrip = await store.ListAsync("tenant-a", null);
        roundTrip.Should().HaveCount(1);
        roundTrip.Single().TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task Create_Returns_400_When_ServiceTypeCode_Missing()
    {
        var (controller, _, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var bad = ValidRequest();
        bad.ServiceTypeCode = "";

        var result = await controller.Create(bad, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Returns_400_When_Rules_Empty()
    {
        var (controller, _, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var bad = ValidRequest();
        bad.Rules = new List<ProcedureCodeRuleDto>();

        var result = await controller.Create(bad, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Returns_400_When_Rules_Exceed_Cap()
    {
        var (controller, _, _) = Build(adminEnabled: true, tenantId: "tenant-a", maxRulesPerMapping: 2);
        var bad = ValidRequest();
        bad.Rules = Enumerable.Range(0, 3)
            .Select(i => new ProcedureCodeRuleDto { CodeType = "CPT", CodePattern = $"9920{i}", Priority = i })
            .ToList();

        var result = await controller.Create(bad, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Returns_400_When_EffectiveEnd_Before_EffectiveStart()
    {
        // Capability BP 5.10 — reject impossible effective windows at
        // the producer boundary so the resolver doesn't silently filter
        // them out during adjudication.
        var (controller, _, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var bad = ValidRequest();
        bad.EffectiveStart = new DateOnly(2026, 6, 1);
        bad.EffectiveEnd = new DateOnly(2026, 1, 1);

        var result = await controller.Create(bad, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Accepts_Valid_Effective_Window()
    {
        var (controller, _, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var ok = ValidRequest();
        ok.EffectiveStart = new DateOnly(2026, 1, 1);
        ok.EffectiveEnd = new DateOnly(2026, 12, 31);

        var result = await controller.Create(ok, default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Update_Returns_400_When_EffectiveEnd_Before_EffectiveStart()
    {
        var (controller, store, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var seeded = await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));
        var bad = ValidRequest();
        bad.EffectiveStart = new DateOnly(2026, 6, 1);
        bad.EffectiveEnd = new DateOnly(2026, 1, 1);

        var result = await controller.Update(seeded.Id, bad, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Returns_404_For_Missing_Mapping()
    {
        var (controller, _, _) = Build(adminEnabled: true, tenantId: "tenant-a");

        var result = await controller.Update(Guid.NewGuid(), ValidRequest(), default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_Replaces_Existing_Mapping()
    {
        var (controller, store, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var seeded = await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));
        var request = ValidRequest();
        request.ServiceTypeCode = "Office Visit (Updated)";

        var result = await controller.Update(seeded.Id, request, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        var refetched = await store.GetByIdAsync("tenant-a", seeded.Id);
        refetched!.ServiceTypeCode.Should().Be("Office Visit (Updated)");
    }

    [Fact]
    public async Task Update_Returns_503_When_Admin_Write_Disabled()
    {
        var (controller, store, _) = Build(adminEnabled: false, tenantId: "tenant-a");
        var seeded = await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));

        var result = await controller.Update(seeded.Id, ValidRequest(), default);

        var status = result.Result as ObjectResult;
        status!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Delete_Removes_Existing_Mapping()
    {
        var (controller, store, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var seeded = await store.CreateAsync(Mapping("tenant-a", null, "Office Visit"));

        var result = await controller.Delete(seeded.Id, default);

        result.Should().BeOfType<NoContentResult>();
        (await store.GetByIdAsync("tenant-a", seeded.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_Returns_404_For_Mismatched_Tenant()
    {
        var (controller, store, _) = Build(adminEnabled: true, tenantId: "tenant-a");
        var seeded = await store.CreateAsync(Mapping("tenant-b", null, "Office Visit"));

        var result = await controller.Delete(seeded.Id, default);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SeedSystemDefaults_Returns_503_When_Admin_Write_Disabled()
    {
        var (controller, _, _) = Build(adminEnabled: false, tenantId: "tenant-a");

        var result = await controller.SeedSystemDefaults(default);

        var status = result.Result as ObjectResult;
        status!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task SeedSystemDefaults_Returns_503_When_Bundle_Failed_To_Load()
    {
        // Build with adminEnabled but with a seeder whose bundle never loaded.
        var (controller, _, seeder) = Build(adminEnabled: true, tenantId: "tenant-a");
        seeder.LoadedBundle.Should().BeNull("default Build does not run StartAsync");

        var result = await controller.SeedSystemDefaults(default);

        var status = result.Result as ObjectResult;
        status!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static ServiceCategoryMappingRequest ValidRequest() => new()
    {
        PlanId = null,
        ServiceTypeCode = "Office Visit",
        ServiceTypeDescription = "Professional E&M visit",
        Rules = new List<ProcedureCodeRuleDto>
        {
            new() { Priority = 10, CodeType = "CPT", CodePattern = "99213" },
        },
    };

    private static ServiceCategoryMapping Mapping(string tenantId, Guid? planId, string code) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        BenefitPlanId = planId,
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        Rules = new List<ProcedureCodeRule>
        {
            new() { Priority = 10, CodeType = "CPT", CodePattern = "99213" },
        },
    };

    private static (
        ServiceCategoryMappingsController controller,
        InMemoryServiceCategoryMappingStore store,
        SystemDefaultMappingSeeder seeder)
        Build(bool adminEnabled, string tenantId, int? maxRulesPerMapping = null)
    {
        var store = new InMemoryServiceCategoryMappingStore();
        var options = new ServiceCategoryMappingOptions
        {
            AdminWriteEnabled = adminEnabled,
            CacheTtl = TimeSpan.Zero,
            SeedSystemDefaultsOnStartup = false,
            MaxRulesPerMapping = maxRulesPerMapping ?? 1_000,
        };
        var monitor = new TestOptionsMonitor<ServiceCategoryMappingOptions>(options);

        // Construct a seeder with a guaranteed-fail bundle path so
        // LoadedBundle stays null without invoking StartAsync — matches
        // the "bundle failed to load" surface that the SeedSystemDefaults
        // 503 branch tests.
        var seedOptions = new ServiceCategoryMappingOptions
        {
            SeedFilePath = Path.Combine(Path.GetTempPath(), $"non-existent-{Guid.NewGuid()}.json"),
            SeedSystemDefaultsOnStartup = false,
        };
        var seederMonitor = new TestOptionsMonitor<ServiceCategoryMappingOptions>(seedOptions);

        var hostEnv = new TestHostEnvironment();
        var sp = new TestServiceProvider(store, store);
        var seeder = new SystemDefaultMappingSeeder(
            sp, seederMonitor, hostEnv, NullLogger<SystemDefaultMappingSeeder>.Instance);

        var controller = new ServiceCategoryMappingsController(
            store, seeder, monitor, NullLogger<ServiceCategoryMappingsController>.Instance);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.HttpContext.Items["TenantId"] = tenantId;
        return (controller, store, seeder);
    }
}

internal sealed class TestServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
{
    private readonly IServiceCategoryMappingWriteRepository _writeRepo;
    private readonly ISystemDefaultsAppliedRecordRepository _appliedRepo;

    public TestServiceProvider(
        IServiceCategoryMappingWriteRepository writeRepo,
        ISystemDefaultsAppliedRecordRepository appliedRepo)
    {
        _writeRepo = writeRepo;
        _appliedRepo = appliedRepo;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceCategoryMappingWriteRepository)) return _writeRepo;
        if (serviceType == typeof(ISystemDefaultsAppliedRecordRepository)) return _appliedRepo;
        if (serviceType == typeof(IServiceScopeFactory)) return this;
        return null;
    }

    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public void Dispose() { }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "BenefitPlanService.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
        = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
