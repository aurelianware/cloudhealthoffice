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
/// D-21 (second part) and G-05. CRD called the prior-authorization rule engine
/// with a hard-coded <c>TX</c> / <c>Medicaid</c> / <c>STAR</c> context, no
/// diagnoses, no member date of birth and a cost of zero — for every member, in
/// every state, on every line of business.
///
/// <para>
/// That is not an incomplete context, it is a wrong one. It resolves a Texas
/// Medicaid STAR rule set for a commercial member in Ohio, and whatever that
/// rule set says is then reported to a provider as CHO's coverage
/// determination. These tests cover reading the context from the member CHO
/// actually holds, and — the half that matters more — refusing to invent one.
/// </para>
/// </summary>
public class CrdContextResolverTests
{
    private const string Tenant = "tenant-crd-context";
    private const string LobSystem = ChoNativeFhirDataAdapter.LineOfBusinessSystem;

    private readonly Mock<IFhirDataAdapter> _adapter = new();

    // ── Reading the real context ─────────────────────────────────────────────

    [Fact]
    public async Task The_state_and_line_of_business_come_from_chos_own_records()
    {
        Holds(MemberIn("OH", "1983-04-02"), CoverageWith(PaLineOfBusiness.Commercial));

        var context = (await ResolveAsync()).Context!;

        context.StateCode.Should().Be("OH");
        context.Lob.Should().Be(PaLineOfBusiness.Commercial);
        context.MemberId.Should().Be("pat-001");
        context.TenantId.Should().Be(Tenant);
    }

    /// <summary>
    /// CHO's coverage model has no state-program field, so <c>Program</c> is
    /// null — which is what <c>PaRuleContext</c> documents for a non-program
    /// state, and what is true: CHO does not know. The old constant made every
    /// member in the country a Texas STAR member.
    /// </summary>
    [Fact]
    public async Task The_state_program_is_null_rather_than_a_constant()
    {
        Holds(MemberIn("OH"), CoverageWith(PaLineOfBusiness.Commercial));

        (await ResolveAsync()).Context!.Program.Should().BeNull();
    }

    /// <summary>
    /// A DiagnosisRequired rule handed an empty list passes vacuously, which is
    /// one of the ways CRD used to fail open.
    /// </summary>
    [Fact]
    public async Task The_diagnoses_come_from_the_draft_orders_reason()
    {
        Holds(MemberIn("TX"), CoverageWith(PaLineOfBusiness.Medicaid));

        var request = Hook();
        request.Context!.DraftOrders!.Entry![0].Resource = new CrdDraftOrderResource
        {
            ResourceType = "ServiceRequest",
            Code = new CrdCodeableConcept { Coding = [new() { Code = "27447" }] },
            ReasonCode =
            [
                new() { Coding = [new() { System = "http://hl7.org/fhir/sid/icd-10-cm", Code = "M17.11" }] },
                new() { Coding = [new() { System = "http://hl7.org/fhir/sid/icd-10-cm", Code = "M17.12" }] },
            ],
        };

        (await ResolveAsync(request)).Context!.DiagnosisCodes
            .Should().BeEquivalentTo(["M17.11", "M17.12"]);
    }

    [Fact]
    public async Task The_member_date_of_birth_is_supplied_for_age_rules()
    {
        Holds(MemberIn("TX", "1983-04-02"), CoverageWith(PaLineOfBusiness.Medicaid));

        (await ResolveAsync()).Context!.MemberDateOfBirth
            .Should().Be(new DateOnly(1983, 4, 2));
    }

    [Fact]
    public async Task The_service_date_comes_from_the_order_when_it_states_one()
    {
        Holds(MemberIn("TX"), CoverageWith(PaLineOfBusiness.Medicaid));

        var request = Hook();
        request.Context!.DraftOrders!.Entry![0].Resource = new CrdDraftOrderResource
        {
            ResourceType = "ServiceRequest",
            Code = new CrdCodeableConcept { Coding = [new() { Code = "27447" }] },
            OccurrenceDateTime = "2027-02-11T09:00:00Z",
        };

        (await ResolveAsync(request)).Context!.ServiceDate.Should().Be(new DateOnly(2027, 2, 11));
    }

    [Fact]
    public async Task A_service_date_only_given_as_a_period_is_read_from_its_start()
    {
        Holds(MemberIn("TX"), CoverageWith(PaLineOfBusiness.Medicaid));

        var request = Hook();
        request.Context!.DraftOrders!.Entry![0].Resource = new CrdDraftOrderResource
        {
            ResourceType = "ServiceRequest",
            Code = new CrdCodeableConcept { Coding = [new() { Code = "27447" }] },
            OccurrencePeriod = new CrdPeriod { Start = "2027-03-01", End = "2027-03-31" },
        };

        (await ResolveAsync(request)).Context!.ServiceDate.Should().Be(new DateOnly(2027, 3, 1));
    }

    [Fact]
    public async Task An_order_with_no_stated_date_is_treated_as_today()
    {
        Holds(MemberIn("TX"), CoverageWith(PaLineOfBusiness.Medicaid));

        (await ResolveAsync()).Context!.ServiceDate
            .Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    // ── Refusing to invent one ───────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_member_resolves_to_no_member_found()
    {
        Holds(patient: null, CoverageWith(PaLineOfBusiness.Medicaid));

        var resolution = await ResolveAsync();

        resolution.IsResolved.Should().BeFalse();
        resolution.Failure.Should().Be(CrdContextFailure.MemberNotFound);
    }

    [Fact]
    public async Task A_hook_with_no_patient_does_not_reach_the_stores_at_all()
    {
        var request = Hook();
        request.Context!.PatientId = null;

        (await ResolveAsync(request)).Failure.Should().Be(CrdContextFailure.MemberNotFound);

        _adapter.Verify(a => a.GetPatientAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_member_with_no_coverage_resolves_to_no_active_coverage()
    {
        Holds(MemberIn("TX"), coverage: null);

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.NoActiveCoverage);
    }

    /// <summary>
    /// The search asks for active coverage. An adapter that ignored the filter
    /// must not be allowed to produce a determination against lapsed coverage —
    /// a confident answer about benefits the member no longer has.
    /// </summary>
    [Fact]
    public async Task A_cancelled_coverage_is_not_treated_as_active_even_if_the_search_returns_it()
    {
        var cancelled = CoverageWith(PaLineOfBusiness.Medicaid);
        cancelled.Status = FinancialResourceStatusCodes.Cancelled;
        Holds(MemberIn("TX"), cancelled);

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.NoActiveCoverage);
    }

    [Fact]
    public async Task A_member_with_no_address_state_resolves_to_an_incomplete_context()
    {
        Holds(new Patient { Id = "pat-001" }, CoverageWith(PaLineOfBusiness.Medicaid));

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.ContextIncomplete);
    }

    [Fact]
    public async Task Coverage_without_a_recognised_line_of_business_resolves_to_an_incomplete_context()
    {
        Holds(MemberIn("TX"), new Coverage
        {
            Id = "cov-001",
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept { Coding = [new Coding(LobSystem, "Interplanetary")] },
        });

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.ContextIncomplete);
    }

    /// <summary>
    /// A line of business is what selects the rule set, so reading it from the
    /// wrong code system is the exact failure this resolver exists to prevent:
    /// another system may use the same word for a different concept.
    /// </summary>
    [Fact]
    public async Task A_line_of_business_coding_from_another_code_system_is_not_read()
    {
        Holds(MemberIn("TX"), new Coverage
        {
            Id = "cov-001",
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept
            {
                Coding = [new Coding("http://example.org/their-own-lob", "Medicaid")],
            },
        });

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.ContextIncomplete);
    }

    /// <summary>
    /// Unreachable is not the same as absent, and the IG has a different reason
    /// code for each. Telling a provider "member not found" when a store was
    /// merely down would tell them to stop looking.
    /// </summary>
    [Fact]
    public async Task A_store_that_is_down_is_reported_as_technical_not_as_a_missing_member()
    {
        _adapter
            .Setup(a => a.GetPatientAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("member-service unavailable"));

        (await ResolveAsync()).Failure.Should().Be(CrdContextFailure.Unavailable);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CrdContextResolution> ResolveAsync(CrdHookRequest? request = null) =>
        new CrdContextResolver(_adapter.Object, NullLogger<CrdContextResolver>.Instance)
            .ResolveAsync(request ?? Hook(), Tenant, ["27447"]);

    private void Holds(Patient? patient, Coverage? coverage)
    {
        _adapter
            .Setup(a => a.GetPatientAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(patient);

        _adapter
            .Setup(a => a.SearchCoverageAsync(
                It.IsAny<CoverageSearchParams>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(coverage is null
                ? ([], 0)
                : ([coverage], 1));
    }

    private static Patient MemberIn(string state, string? birthDate = null) => new()
    {
        Id = "pat-001",
        BirthDate = birthDate,
        Address = [new Address { State = state }],
    };

    private static Coverage CoverageWith(PaLineOfBusiness lob) => new()
    {
        Id = "cov-001",
        Status = FinancialResourceStatusCodes.Active,
        Type = new CodeableConcept { Coding = [new Coding(LobSystem, lob.ToString())] },
    };

    private static CrdHookRequest Hook() => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-sign",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/1234567893",
            PatientId = "Patient/pat-001",
            DraftOrders = new CrdDraftOrders
            {
                Entry =
                [
                    new()
                    {
                        Resource = new CrdDraftOrderResource
                        {
                            ResourceType = "ServiceRequest",
                            Code = new CrdCodeableConcept { Coding = [new() { Code = "27447" }] },
                        },
                    },
                ],
            },
        },
    };
}
