using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.ChoNative;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Where the rule engine's three keys come from once programs exist
/// (docs/architecture/plan-configuration.md §12 step 2).
///
/// <para>
/// The engine keys its rule sets on state, line of business and program, and a
/// program record carries all three. #1511 left <c>Program</c> null because CHO
/// had no program field to read: state came from the member's mailing address
/// and line of business from the coverage's own coding. That is a correct
/// answer, but a rule set keyed on <c>STAR</c> fired for nobody. With a program
/// on the member's benefit plan, all three come from the one record that states
/// what was actually sold.
/// </para>
///
/// <para>
/// The failure case is the point of the whole resolver: if the program could
/// not be looked up, the context is <b>not</b> filled in from the member's
/// address instead. A member on a Texas STAR plan who lives in Ohio would
/// otherwise get an Ohio rule set during a tenant-service outage — a confident
/// determination about somebody else's benefits, which is the exact failure
/// #1511 removed.
/// </para>
/// </summary>
public class CrdContextProgramTests
{
    private const string Tenant = "tenant-crd-program";
    private const string LobSystem = ChoNativeFhirDataAdapter.LineOfBusinessSystem;
    private const string ClassSystem = CrdContextResolver.CoverageClassSystem;

    private readonly Mock<IFhirDataAdapter> _adapter = new();
    private readonly InMemoryProgramDirectory _programs = new();

    private static readonly ProgramDescriptor TexasStar = new()
    {
        Id = "t|DEFAULT|STAR",
        Code = "STAR",
        Name = "Texas STAR",
        CarrierId = "t|DEFAULT",
        LineOfBusiness = LinesOfBusiness.Medicaid,
        StateCode = "TX",
    };

    [Fact]
    public async Task All_three_keys_come_from_the_program_the_plan_is_sold_under()
    {
        // The member's address says Ohio and the coverage coding says Medicaid;
        // the program says Texas STAR, and the program is what was sold.
        _programs.Add(Tenant, TexasStar, "PLAN-1");
        Holds(MemberIn("OH"), CoverageWith(PaLineOfBusiness.Medicaid, planId: "PLAN-1"));

        var context = (await ResolveAsync()).Context!;

        context.StateCode.Should().Be("TX");
        context.Lob.Should().Be(PaLineOfBusiness.Medicaid);
        context.Program.Should().Be("STAR");
        // The record behind the code travels too, so a decision made on these
        // rules names a program that can be looked up afterwards
        // (plan-configuration.md §12.6).
        context.ProgramId.Should().Be(TexasStar.Id);
    }

    [Fact]
    public async Task A_plan_with_no_program_falls_back_to_the_address_and_the_coverage_coding()
    {
        // Nothing is registered for PLAN-1, so the directory answers "none".
        Holds(MemberIn("OH"), CoverageWith(PaLineOfBusiness.Commercial, planId: "PLAN-1"));

        var context = (await ResolveAsync()).Context!;

        context.StateCode.Should().Be("OH");
        context.Lob.Should().Be(PaLineOfBusiness.Commercial);
        context.Program.Should().BeNull();
        context.ProgramId.Should().BeNull("there is no program record to name");
    }

    [Fact]
    public async Task A_coverage_that_names_no_plan_is_not_looked_up_at_all()
    {
        Holds(MemberIn("OH"), CoverageWith(PaLineOfBusiness.Commercial, planId: null));

        var context = (await ResolveAsync()).Context!;

        context.StateCode.Should().Be("OH");
        context.Program.Should().BeNull();
        _programs.Calls.Should().Be(0, "there is no plan to follow, so there is nothing to ask");
    }

    [Fact]
    public async Task The_group_class_is_not_mistaken_for_the_plan_class()
    {
        _programs.Add(Tenant, TexasStar, "ACME");
        var coverage = CoverageWith(PaLineOfBusiness.Commercial, planId: null);
        coverage.Class = [Class("group", "ACME")];
        Holds(MemberIn("OH"), coverage);

        var context = (await ResolveAsync()).Context!;

        context.Program.Should().BeNull();
        context.StateCode.Should().Be("OH");
    }

    [Fact]
    public async Task A_directory_that_is_down_is_reported_as_technical_rather_than_answered_from_the_address()
    {
        _programs.Unavailable = true;
        Holds(MemberIn("OH"), CoverageWith(PaLineOfBusiness.Medicaid, planId: "PLAN-1"));

        var resolution = await ResolveAsync();

        resolution.Failure.Should().Be(CrdContextFailure.Unavailable);
        resolution.Context.Should().BeNull();
    }

    [Fact]
    public async Task A_program_whose_line_of_business_the_engine_has_no_member_for_resolves_nothing()
    {
        // CHO's own lines of business all map, so this is a program record
        // written by a build that knew a line this one does not. Better no
        // determination than one from a neighbouring line of business — and
        // better than quietly reverting to the coverage's coding, which would
        // keep the wrong state.
        _programs.Add(Tenant, TexasStar with { LineOfBusiness = "Dental" }, "PLAN-1");
        Holds(MemberIn("TX"), CoverageWith(PaLineOfBusiness.Medicaid, planId: "PLAN-1"));

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.ContextIncomplete);
    }

    [Fact]
    public async Task A_host_with_no_program_directory_behaves_exactly_as_before()
    {
        Holds(MemberIn("OH"), CoverageWith(PaLineOfBusiness.Commercial, planId: "PLAN-1"));

        var context = (await new CrdContextResolver(_adapter.Object, NullLogger<CrdContextResolver>.Instance)
            .ResolveAsync(Hook(), Tenant, ["27447"])).Context!;

        context.StateCode.Should().Be("OH");
        context.Program.Should().BeNull();
    }

    [Fact]
    public void A_plan_id_is_read_from_the_plan_class_whatever_order_the_classes_are_in()
    {
        var coverage = new Coverage { Class = [Class("group", "ACME"), Class("plan", " PLAN-1 ")] };

        CrdContextResolver.BenefitPlanId(coverage).Should().Be("PLAN-1");
        CrdContextResolver.BenefitPlanId(new Coverage()).Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CrdContextResolution> ResolveAsync() =>
        new CrdContextResolver(_adapter.Object, NullLogger<CrdContextResolver>.Instance, _programs)
            .ResolveAsync(Hook(), Tenant, ["27447"]);

    private void Holds(Patient patient, Coverage coverage)
    {
        _adapter
            .Setup(a => a.GetPatientAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(patient);
        _adapter
            .Setup(a => a.SearchCoverageAsync(
                It.IsAny<CoverageSearchParams>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([coverage], 1));
    }

    private static Patient MemberIn(string state) => new()
    {
        Id = "pat-001",
        BirthDate = "1983-04-02",
        Address = [new Address { State = state }],
    };

    private static Coverage CoverageWith(PaLineOfBusiness lob, string? planId) => new()
    {
        Id = "cov-001",
        Status = FinancialResourceStatusCodes.Active,
        Type = new CodeableConcept { Coding = [new Coding(LobSystem, lob.ToString())] },
        Class = planId is null ? null : [Class("plan", planId)],
    };

    private static Coverage.ClassComponent Class(string code, string value) => new()
    {
        Type = new CodeableConcept { Coding = [new Coding(ClassSystem, code)] },
        Value = value,
    };

    private static CrdHookRequest Hook() => new()
    {
        Hook = "order-select",
        Context = new CrdHookContext
        {
            PatientId = "Patient/pat-001",
            UserId = "Practitioner/1234567893",
            DraftOrders = new CrdDraftOrders
            {
                Entry = [new CrdDraftOrderEntry
                {
                    Resource = new CrdDraftOrderResource
                    {
                        ResourceType = "ServiceRequest",
                        Code = new CrdCodeableConcept { Coding = [new() { Code = "27447" }] },
                    },
                }],
            },
        },
    };
}
