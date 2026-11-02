using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.TenantService.Tests.Configuration;
using CloudHealthOffice.TenantService.Tests.Hierarchy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TenantService.Controllers;
using TenantService.Hierarchy;

namespace CloudHealthOffice.TenantService.Tests.Controllers;

/// <summary>
/// The carrier and program endpoints: each hierarchy outcome becomes the right
/// status code, the tenant always comes from the caller's token, and a
/// benefit-plan-service outage reads as 503, never as "no program".
/// </summary>
public class HierarchyControllersTests
{
    private readonly InMemoryHierarchyStore _store = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc));
    private readonly FakeBenefitPlans _plans = new();

    private HierarchyService Service(IBenefitPlanProgramLookup? plans = null) =>
        HierarchyTestKit.Service(_store, _clock, plans ?? _plans,
            new StubTenants(HierarchyTestKit.TenantRecord(), HierarchyTestKit.TenantRecord("t2")));

    private CarriersController Carriers(TestActor actor) => new(Service(), actor);
    private ProgramsController Programs(TestActor actor, IBenefitPlanProgramLookup? plans = null) => new(Service(plans), actor);

    private static int? Status(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private async Task<PlanProgram> CreateStar()
    {
        var created = await Programs(HierarchyTestKit.Manager()).Create(HierarchyTestKit.TexasStar, default);
        return (PlanProgram)((CreatedAtActionResult)created).Value!;
    }

    // ── Carriers ───────────────────────────────────────────────────────

    [Fact]
    public async Task Listing_carriers_returns_the_callers_default_carrier()
    {
        var result = await Carriers(HierarchyTestKit.Analyst()).List(default);

        var carriers = (IReadOnlyList<Carrier>)((OkObjectResult)result).Value!;
        carriers.Should().ContainSingle().Which.TenantId.Should().Be(HierarchyTestKit.Tenant);
    }

    [Fact]
    public async Task A_manager_creates_a_carrier_and_gets_201_pointing_at_it()
    {
        var result = await Carriers(HierarchyTestKit.Manager()).Create(new CarrierWrite { Code = "ins", Name = "Acme Insurance" }, default);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(CarriersController.Get));
        var carrier = (Carrier)created.Value!;
        created.RouteValues!["id"].Should().Be(carrier.Id);

        var read = await Carriers(HierarchyTestKit.Analyst()).Get(carrier.Id, default);
        read.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task An_analyst_cannot_create_a_carrier()
    {
        var result = await Carriers(HierarchyTestKit.Analyst()).Create(new CarrierWrite { Code = "ins", Name = "x" }, default);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
        _store.Carriers.Should().NotContain(c => c.Code == "ins");
    }

    [Fact]
    public async Task A_duplicate_carrier_code_is_409()
    {
        var controller = Carriers(HierarchyTestKit.Manager());
        await controller.Create(new CarrierWrite { Code = "ins", Name = "One" }, default);

        var result = await controller.Create(new CarrierWrite { Code = "ins", Name = "Two" }, default);

        Status(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task A_carrier_with_no_body_is_422_not_a_crash()
    {
        var result = await Carriers(HierarchyTestKit.Manager()).Create(null!, default);

        Status(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task Another_tenants_carrier_is_404_even_by_its_exact_id()
    {
        var created = await Carriers(HierarchyTestKit.Manager()).Create(new CarrierWrite { Code = "ins", Name = "Acme" }, default);
        var id = ((Carrier)((CreatedAtActionResult)created).Value!).Id;

        var result = await Carriers(HierarchyTestKit.Analyst("t2")).Get(id, default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Another_tenants_manager_cannot_update_the_carrier()
    {
        var created = await Carriers(HierarchyTestKit.Manager()).Create(new CarrierWrite { Code = "ins", Name = "Acme" }, default);
        var id = ((Carrier)((CreatedAtActionResult)created).Value!).Id;

        var result = await Carriers(HierarchyTestKit.Manager("t2")).Update(id, new CarrierWrite { Name = "Hijacked" }, default);

        Status(result).Should().Be(StatusCodes.Status404NotFound);
        _store.Carriers.Single(c => c.Id == id).Name.Should().Be("Acme");
    }

    [Fact]
    public async Task A_manager_updates_their_own_carrier()
    {
        var created = await Carriers(HierarchyTestKit.Manager()).Create(new CarrierWrite { Code = "ins", Name = "Acme" }, default);
        var id = ((Carrier)((CreatedAtActionResult)created).Value!).Id;

        var result = await Carriers(HierarchyTestKit.Manager()).Update(id, new CarrierWrite { Name = "Acme Insurance" }, default);

        ((Carrier)((OkObjectResult)result).Value!).Name.Should().Be("Acme Insurance");
    }

    // ── Programs ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_analyst_creates_a_program_under_the_default_carrier()
    {
        var program = await CreateStar();

        program.TenantId.Should().Be(HierarchyTestKit.Tenant);
        var list = await Programs(HierarchyTestKit.Analyst()).List(null, default);
        ((IReadOnlyList<PlanProgram>)((OkObjectResult)list).Value!).Should().ContainSingle(p => p.Id == program.Id);
    }

    [Fact]
    public async Task A_caller_without_product_rights_cannot_create_a_program()
    {
        var reader = new TestActor("reader", HierarchyTestKit.Tenant, "ClaimsExaminer");

        var result = await Programs(reader).Create(HierarchyTestKit.TexasStar, default);

        Status(result).Should().Be(StatusCodes.Status403Forbidden);
        _store.Programs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_duplicate_program_code_is_409()
    {
        await CreateStar();

        var result = await Programs(HierarchyTestKit.Manager()).Create(HierarchyTestKit.TexasStar, default);

        Status(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_or_change_the_program()
    {
        var program = await CreateStar();

        (await Programs(HierarchyTestKit.Analyst("t2")).Get(program.Id, default)).Should().BeOfType<NotFoundObjectResult>();
        var update = await Programs(HierarchyTestKit.Manager("t2")).Update(program.Id, new ProgramWrite { Name = "Hijacked" }, default);

        Status(update).Should().Be(StatusCodes.Status404NotFound);
        _store.Programs.Single().Name.Should().Be("Texas STAR");
    }

    [Fact]
    public async Task Updating_a_program_returns_it()
    {
        var program = await CreateStar();

        var result = await Programs(HierarchyTestKit.Manager()).Update(program.Id, new ProgramWrite { Name = "Texas STAR Kids" }, default);

        ((PlanProgram)((OkObjectResult)result).Value!).Name.Should().Be("Texas STAR Kids");
    }

    [Fact]
    public async Task A_benefit_plan_with_a_program_returns_it()
    {
        var program = await CreateStar();
        _plans.Programs["plan-1"] = program.Id;

        var result = await Programs(HierarchyTestKit.Analyst()).ForBenefitPlan("plan-1", default);

        ((PlanProgram)((OkObjectResult)result).Value!).Id.Should().Be(program.Id);
    }

    [Fact]
    public async Task A_benefit_plan_without_a_program_is_404()
    {
        _plans.Programs["plan-1"] = null;

        var result = await Programs(HierarchyTestKit.Analyst()).ForBenefitPlan("plan-1", default);

        Status(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task An_unreachable_benefit_plan_service_is_503_not_404()
    {
        _plans.Fail = true;

        var result = await Programs(HierarchyTestKit.Analyst()).ForBenefitPlan("plan-1", default);

        Status(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task A_host_without_benefit_plan_lookups_is_503()
    {
        var controller = new ProgramsController(
            HierarchyTestKit.Service(_store, _clock, benefitPlans: null), HierarchyTestKit.Analyst());

        var result = await controller.ForBenefitPlan("plan-1", default);

        Status(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
    }
}
