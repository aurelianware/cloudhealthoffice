using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.TenantService.Tests.Configuration;
using TenantService.Hierarchy;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// Carrier and program records (plan-configuration.md §12 step 2): the default
/// carrier, what may and may not be changed once a program exists, who may
/// change what, and following a benefit plan to its program.
/// </summary>
public class HierarchyServiceTests
{
    private readonly InMemoryHierarchyStore _store = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc));
    private readonly FakeBenefitPlans _plans = new();

    private HierarchyService Service() => HierarchyTestKit.Service(_store, _clock, _plans);

    // ── The default carrier ────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_with_no_carrier_gets_one_from_its_own_record()
    {
        var carriers = await Service().ListCarriersAsync(HierarchyTestKit.Tenant);

        carriers.Should().ContainSingle();
        carriers[0].Should().Match<Carrier>(c =>
            c.Code == HierarchyService.DefaultCarrierCode
            && c.IsDefault
            && c.Active
            && c.Name == "Acme Health Plan, Inc."
            && c.LegalName == "Acme Health Plan, Inc."
            && c.CorrespondenceAddress!.State == "TX");
    }

    [Fact]
    public async Task Listing_twice_does_not_create_a_second_default_carrier()
    {
        var service = Service();
        await service.ListCarriersAsync(HierarchyTestKit.Tenant);
        await service.ListCarriersAsync(HierarchyTestKit.Tenant);

        _store.Carriers.Should().ContainSingle();
    }

    [Fact]
    public async Task A_default_carrier_another_request_created_first_is_read_rather_than_duplicated()
    {
        // The insert loses the race; the service must return the winner's record.
        _store.Carriers.Add(new Carrier
        {
            Id = Carrier.IdFor(HierarchyTestKit.Tenant, HierarchyService.DefaultCarrierCode),
            TenantId = HierarchyTestKit.Tenant, Code = HierarchyService.DefaultCarrierCode, Name = "Winner", IsDefault = true,
        });
        _store.FailNextCarrierInsert = true;

        var carrier = await Service().EnsureDefaultCarrierAsync(HierarchyTestKit.Tenant);

        carrier!.Name.Should().Be("Winner");
        _store.Carriers.Should().ContainSingle();
    }

    [Fact]
    public async Task A_tenant_that_does_not_exist_gets_no_carrier()
    {
        (await Service().EnsureDefaultCarrierAsync("nobody")).Should().BeNull();
        (await Service().ListCarriersAsync("nobody")).Should().BeEmpty();
    }

    // ── Carriers ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_carrier_needs_enterprise_configuration()
    {
        var write = new CarrierWrite { Code = "ins", Name = "Acme Insurance Company" };

        (await Service().CreateCarrierAsync(HierarchyTestKit.Analyst(), write)).Outcome.Should().Be(HierarchyOutcome.Forbidden);

        var created = await Service().CreateCarrierAsync(HierarchyTestKit.Manager(), write);
        created.Outcome.Should().Be(HierarchyOutcome.Ok);
        created.Value!.Code.Should().Be("INS", "codes are upper case");
        created.Value.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Two_carriers_cannot_share_a_code()
    {
        var write = new CarrierWrite { Code = "INS", Name = "Acme Insurance Company" };
        await Service().CreateCarrierAsync(HierarchyTestKit.Manager(), write);

        (await Service().CreateCarrierAsync(HierarchyTestKit.Manager(), write)).Outcome.Should().Be(HierarchyOutcome.Conflict);
    }

    [Theory]
    [InlineData("", "code must")]
    [InlineData("has space", "code must")]
    public async Task A_carrier_code_is_checked(string code, string expected)
    {
        var result = await Service().CreateCarrierAsync(HierarchyTestKit.Manager(), new CarrierWrite { Code = code, Name = "x" });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.StartsWith(expected));
    }

    [Theory]
    [InlineData("12-3456789")]
    [InlineData("1234567")]
    public async Task A_tax_id_must_be_nine_digits(string taxId)
    {
        var result = await Service().CreateCarrierAsync(HierarchyTestKit.Manager(),
            new CarrierWrite { Code = "INS", Name = "x", TaxId = taxId });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.StartsWith("taxId"));
    }

    [Fact]
    public async Task Legal_entity_details_are_stored_on_the_carrier()
    {
        var result = await Service().CreateCarrierAsync(HierarchyTestKit.Manager(), new CarrierWrite
        {
            Code = "HMO",
            Name = "Acme HMO",
            LegalName = "Acme Health Maintenance Organization",
            TaxId = "123456789",
            NaicCode = "12345",
            PayerIds = new Dictionary<string, string> { ["837"] = "ACME837", ["270"] = "ACME270" },
            NachaImmediateOrigin = "1123456789",
            NachaCompanyIdentification = "1123456789",
            CorrespondenceName = "Acme HMO Member Services",
        });

        result.Value!.PayerIds.Should().ContainKey("837").WhoseValue.Should().Be("ACME837");
        result.Value.TaxId.Should().Be("123456789");
        result.Value.CreatedAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task A_carriers_code_cannot_be_changed()
    {
        var created = await Service().CreateCarrierAsync(HierarchyTestKit.Manager(), new CarrierWrite { Code = "INS", Name = "x" });

        var result = await Service().UpdateCarrierAsync(HierarchyTestKit.Manager(), created.Value!.Id, new CarrierWrite { Code = "OTHER" });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("code cannot be changed"));
    }

    [Fact]
    public async Task A_tenant_cannot_retire_its_only_carrier()
    {
        var service = Service();
        var carriers = await service.ListCarriersAsync(HierarchyTestKit.Tenant);

        var result = await service.UpdateCarrierAsync(HierarchyTestKit.Manager(), carriers[0].Id, new CarrierWrite { Active = false });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("one active carrier"));
    }

    [Fact]
    public async Task A_carrier_can_be_retired_once_another_is_active()
    {
        var service = Service();
        var carriers = await service.ListCarriersAsync(HierarchyTestKit.Tenant);
        await service.CreateCarrierAsync(HierarchyTestKit.Manager(), new CarrierWrite { Code = "INS", Name = "Acme Insurance" });

        var result = await service.UpdateCarrierAsync(HierarchyTestKit.Manager(), carriers[0].Id, new CarrierWrite { Active = false });

        result.Outcome.Should().Be(HierarchyOutcome.Ok);
        result.Value!.Active.Should().BeFalse();
    }

    [Fact]
    public async Task An_edit_that_lost_a_race_is_refused_rather_than_overwriting()
    {
        var service = Service();
        var carrier = (await service.ListCarriersAsync(HierarchyTestKit.Tenant))[0];
        // Another writer moved the record on between this caller's read and its write.
        (await _store.TryReplaceCarrierAsync(Clone(carrier, "Theirs"), carrier.Revision)).Should().BeTrue();

        var result = await _store.TryReplaceCarrierAsync(Clone(carrier, "Ours"), carrier.Revision);

        result.Should().BeFalse();
        _store.Carriers[0].Name.Should().Be("Theirs");
    }

    private static Carrier Clone(Carrier carrier, string name) => new()
    {
        Id = carrier.Id, TenantId = carrier.TenantId, Code = carrier.Code, Name = name,
        IsDefault = carrier.IsDefault, Active = carrier.Active, Revision = carrier.Revision,
    };

    // ── Programs ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_program_goes_under_the_default_carrier_when_none_is_named()
    {
        var result = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar);

        result.Outcome.Should().Be(HierarchyOutcome.Ok);
        result.Value.Should().NotBeNull();
        result.Value!.Should().Match<PlanProgram>(p =>
            p.Code == "STAR"
            && p.StateCode == "TX"
            && p.LineOfBusiness == LinesOfBusiness.Medicaid
            && p.CarrierId == Carrier.IdFor(HierarchyTestKit.Tenant, HierarchyService.DefaultCarrierCode)
            && p.Active);
        result.Value!.Id.Should().Be($"{HierarchyTestKit.Tenant}|DEFAULT|STAR");
    }

    [Fact]
    public async Task A_program_needs_product_configuration()
    {
        var outsider = new TestActor("reader", HierarchyTestKit.Tenant, "ComplianceViewer");

        (await Service().CreateProgramAsync(outsider, HierarchyTestKit.TexasStar)).Outcome.Should().Be(HierarchyOutcome.Forbidden);
    }

    [Theory]
    [InlineData("Texas", "stateCode")]
    [InlineData("T", "stateCode")]
    public async Task A_programs_state_must_be_a_two_letter_code(string state, string field)
    {
        var result = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with { StateCode = state });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.StartsWith(field));
    }

    [Fact]
    public async Task A_programs_line_of_business_must_be_one_CHO_knows()
    {
        var result = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with { LineOfBusiness = "Dental" });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.StartsWith("lineOfBusiness"));
    }

    [Fact]
    public async Task Line_of_business_is_stored_canonically_whichever_spelling_arrives()
    {
        var result = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with { LineOfBusiness = "medicaid" });

        result.Value!.LineOfBusiness.Should().Be(LinesOfBusiness.Medicaid);
    }

    [Fact]
    public async Task Two_programs_under_one_carrier_cannot_share_a_code()
    {
        await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar);

        var again = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar);

        again.Outcome.Should().Be(HierarchyOutcome.Conflict);
    }

    [Fact]
    public async Task One_carrier_can_run_several_programs_in_one_state()
    {
        var service = Service();
        await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar);
        await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with { Code = "STARKIDS", Name = "Texas STAR Kids" });
        await service.CreateProgramAsync(HierarchyTestKit.Analyst(),
            new ProgramWrite { Code = "LG", Name = "Arizona Large Group", LineOfBusiness = LinesOfBusiness.Commercial, StateCode = "AZ" });

        var programs = await service.ListProgramsAsync(HierarchyTestKit.Tenant, carrierId: null);

        programs.Select(p => p.Code).Should().Equal("LG", "STAR", "STARKIDS");
    }

    [Fact]
    public async Task A_program_cannot_be_added_to_a_retired_carrier()
    {
        var service = Service();
        await service.ListCarriersAsync(HierarchyTestKit.Tenant);
        var second = await service.CreateCarrierAsync(HierarchyTestKit.Manager(), new CarrierWrite { Code = "OLD", Name = "Old entity" });
        var retired = await service.UpdateCarrierAsync(HierarchyTestKit.Manager(), second.Value!.Id, new CarrierWrite { Active = false });
        retired.Value!.Active.Should().BeFalse();

        var result = await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with { CarrierId = second.Value.Id });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("retired"));
    }

    [Fact]
    public async Task A_program_under_a_carrier_the_tenant_does_not_have_is_refused()
    {
        var result = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with { CarrierId = "t1|NOPE" });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("No carrier"));
    }

    [Theory]
    [InlineData("code")]
    [InlineData("state")]
    [InlineData("lineOfBusiness")]
    [InlineData("carrier")]
    public async Task What_a_rule_set_is_keyed_on_cannot_be_changed_under_it(string field)
    {
        var service = Service();
        var program = (await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar)).Value!;
        var write = field switch
        {
            "code" => new ProgramWrite { Code = "OTHER" },
            "state" => new ProgramWrite { StateCode = "OH" },
            "lineOfBusiness" => new ProgramWrite { LineOfBusiness = LinesOfBusiness.Commercial },
            _ => new ProgramWrite { CarrierId = "t1|OTHER" },
        };

        var result = await service.UpdateProgramAsync(HierarchyTestKit.Analyst(), program.Id, write);

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.Contains("create a program instead"));
    }

    [Fact]
    public async Task A_programs_name_dates_and_status_can_be_changed()
    {
        var service = Service();
        var program = (await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar)).Value!;

        var result = await service.UpdateProgramAsync(HierarchyTestKit.Analyst(), program.Id, new ProgramWrite
        {
            Name = "Texas STAR (HHSC)",
            RegulatoryContractNumber = "HHS000123",
            EffectiveFrom = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Active = false,
        });

        result.Outcome.Should().Be(HierarchyOutcome.Ok);
        result.Value!.Should().Match<PlanProgram>(p =>
            p.Name == "Texas STAR (HHSC)" && p.RegulatoryContractNumber == "HHS000123" && !p.Active
            && p.EffectiveFrom == new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task An_end_date_before_the_start_is_refused()
    {
        var result = await Service().CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar with
        {
            EffectiveFrom = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EffectiveTo = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        result.Outcome.Should().Be(HierarchyOutcome.Invalid);
        result.Errors.Should().Contain(e => e.StartsWith("effectiveTo"));
    }

    [Fact]
    public async Task One_tenants_programs_are_invisible_to_another()
    {
        var tenants = new StubTenants(HierarchyTestKit.TenantRecord(), HierarchyTestKit.TenantRecord("t2"));
        var service = HierarchyTestKit.Service(_store, _clock, _plans, tenants);
        await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar);

        (await service.ListProgramsAsync("t2", null)).Should().BeEmpty();
        (await service.GetProgramAsync("t2", $"{HierarchyTestKit.Tenant}|DEFAULT|STAR")).Should().BeNull();
    }

    // ── Following a benefit plan to its program ────────────────────────

    [Fact]
    public async Task A_benefit_plans_program_is_followed_through_benefit_plan_service()
    {
        var service = Service();
        var program = (await service.CreateProgramAsync(HierarchyTestKit.Analyst(), HierarchyTestKit.TexasStar)).Value!;
        _plans.Programs["PLAN-1"] = program.Id;

        var result = await service.ProgramForBenefitPlanAsync(HierarchyTestKit.Tenant, "PLAN-1");

        result.Outcome.Should().Be(HierarchyOutcome.Ok);
        result.Value!.ToDescriptor().Should().Match<ProgramDescriptor>(d =>
            d.Code == "STAR" && d.StateCode == "TX" && d.LineOfBusiness == LinesOfBusiness.Medicaid);
    }

    [Fact]
    public async Task A_plan_with_no_program_is_not_found()
    {
        _plans.Programs["PLAN-1"] = null;

        (await Service().ProgramForBenefitPlanAsync(HierarchyTestKit.Tenant, "PLAN-1")).Outcome
            .Should().Be(HierarchyOutcome.NotFound);
    }

    [Fact]
    public async Task A_plan_naming_a_program_the_tenant_does_not_have_is_not_found()
    {
        _plans.Programs["PLAN-1"] = "t1|DEFAULT|GHOST";

        (await Service().ProgramForBenefitPlanAsync(HierarchyTestKit.Tenant, "PLAN-1")).Outcome
            .Should().Be(HierarchyOutcome.NotFound);
    }

    [Fact]
    public async Task An_unreachable_benefit_plan_service_is_a_conflict_not_an_absent_program()
    {
        // The whole point: a caller must never read an outage as "no program".
        _plans.Fail = true;

        var result = await Service().ProgramForBenefitPlanAsync(HierarchyTestKit.Tenant, "PLAN-1");

        result.Outcome.Should().Be(HierarchyOutcome.Conflict);
        result.Outcome.Should().NotBe(HierarchyOutcome.NotFound);
    }

    [Fact]
    public async Task A_host_without_benefit_plan_lookups_says_so_rather_than_answering_none()
    {
        var service = new HierarchyService(_store, new StubTenants(HierarchyTestKit.TenantRecord()), _clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HierarchyService>.Instance, benefitPlans: null);

        (await service.ProgramForBenefitPlanAsync(HierarchyTestKit.Tenant, "PLAN-1")).Outcome.Should().Be(HierarchyOutcome.Conflict);
    }
}
