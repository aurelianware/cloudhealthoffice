using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests;

/// <summary>
/// The pricing path's end of the MS-DRG grouper seam (ADR 024 step 8).
///
/// <para>
/// The point of these is that installing the seam moved no money. A claim that
/// carries its DRG is priced by it and no grouper is consulted; a claim that
/// does not and says nothing about the stay is refused in exactly the words it
/// was refused in before; and only a claim that asks to be grouped is grouped.
/// </para>
/// </summary>
public class InpatientGroupingTests
{
    private const string Schedule = "MEDICARE_DRG_2025";

    /// <summary>An <see cref="IMsDrgGrouper"/> that records what it was asked.</summary>
    private sealed class StubGrouper(MsDrgGroupingResult answer) : IMsDrgGrouper
    {
        public MsDrgGroupingRequest? Asked { get; private set; }

        public int Calls { get; private set; }

        public Task<MsDrgGroupingResult> GroupAsync(
            MsDrgGroupingRequest request, CancellationToken cancellationToken = default)
        {
            Asked = request;
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private static MsDrgGroupingResult Grouped(string drg, params string[] notes) => new()
    {
        Status = GroupingStatus.Grouped,
        Drg = drg,
        Source = DrgSource.ChoGrouper,
        GrouperVersion = "V440",
        Notes = notes
    };

    [Fact]
    public async Task A_claim_carrying_its_own_drg_is_not_grouped()
    {
        var grouper = new StubGrouper(Grouped("999"));
        var service = ServiceWith(grouper, drg: "470", weight: 1.9m, baseRate: 7000m);

        var result = await service.RepriceClaimAsync(Request(drgCode: "470"));

        grouper.Calls.Should().Be(0, "a DRG on the claim is the DRG that prices it");
        result.DrgCode.Should().Be("470");
        result.TotalAllowed.Should().Be(13300.00m);
    }

    [Fact]
    public async Task A_claim_with_no_drg_and_no_stay_detail_is_refused_as_before()
    {
        var grouper = new StubGrouper(Grouped("470"));
        var service = ServiceWith(grouper, drg: "470", weight: 1.9m, baseRate: 7000m);

        var result = await service.RepriceClaimAsync(Request(drgCode: null));

        grouper.Calls.Should().Be(0, "nothing on the claim says enough about the stay to group it");
        result.Lines[0].Status.Should().Be(PricingStatus.NotFound);
        result.Warnings.Should().Contain(w => w.Contains("No DRG code provided"));
        result.Warnings.Should().Contain(w => w.Contains("InpatientStay"),
            "the refusal should say how to get the claim grouped");
    }

    [Fact]
    public async Task A_grouped_drg_prices_the_claim_and_is_reported()
    {
        var grouper = new StubGrouper(Grouped("470"));
        var service = ServiceWith(grouper, drg: "470", weight: 1.9m, baseRate: 7000m);

        var result = await service.RepriceClaimAsync(Request(drgCode: null, stay: Stay()));

        grouper.Calls.Should().Be(1);

        // The ordering guard: loading an MS-DRG schedule fetches that DRG's
        // rate row, so a grouping done after the load would assign a DRG the
        // schedule holds no rate for and the claim would price at nothing.
        result.DrgCode.Should().Be("470");
        result.TotalAllowed.Should().Be(13300.00m);
        result.Lines[0].Status.Should().Be(PricingStatus.Priced);
    }

    [Fact]
    public async Task A_claim_that_cannot_be_grouped_is_refused_with_the_groupers_reason()
    {
        var refused = MsDrgGroupingResult.NotGrouped(
            GroupingStatus.InvalidPrincipalDiagnosis, "V440", null, "The V440 tables do not hold ZZZZZ.");
        var service = ServiceWith(new StubGrouper(refused), drg: "470", weight: 1.9m, baseRate: 7000m);

        var result = await service.RepriceClaimAsync(Request(drgCode: null, stay: Stay()));

        result.Lines[0].Status.Should().Be(PricingStatus.NotFound);
        result.Warnings.Should().Contain(w => w.Contains("InvalidPrincipalDiagnosis"));
        result.Warnings.Should().Contain(w => w.Contains("ZZZZZ"));
    }

    [Fact]
    public async Task The_groupers_notes_reach_the_caller_on_a_priced_claim()
    {
        var grouper = new StubGrouper(Grouped(
            "470", "The hospital-acquired-condition policy moved the stay from DRG 469 to 470."));
        var service = ServiceWith(grouper, drg: "470", weight: 1.9m, baseRate: 7000m);

        var result = await service.RepriceClaimAsync(Request(drgCode: null, stay: Stay()));

        result.Lines[0].Status.Should().Be(PricingStatus.Priced);
        result.Warnings.Should().Contain(w => w.Contains("hospital-acquired-condition policy"));
    }

    [Fact]
    public async Task A_professional_claim_is_never_grouped()
    {
        var grouper = new StubGrouper(Grouped("470"));
        var repo = new Mock<IFeeScheduleRepository>();
        repo.Setup(r => r.GetScheduleInfoAsync("MEDICARE_RBRVS_2025"))
            .ReturnsAsync(Info("MEDICARE_RBRVS_2025", FeeScheduleType.MedicareRbrvs));
        repo.Setup(r => r.LookupCodesAsync("MEDICARE_RBRVS_2025", It.IsAny<List<string>>(), It.IsAny<string?>()))
            .ReturnsAsync([]);

        var service = Service(repo, grouper);
        await service.RepriceClaimAsync(new RepricingRequest
        {
            FeeScheduleId = "MEDICARE_RBRVS_2025",
            ClaimType = ClaimType.Professional,
            PrimaryDiagnosis = "E1165",
            InpatientStay = Stay(),
            Lines = [new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213" }]
        });

        grouper.Calls.Should().Be(0, "an outpatient claim does not price by DRG");
    }

    // ─────────────────────────────────────────────────────────
    //  Translation into the grouper's request
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_stay_is_translated_into_the_groupers_own_request()
    {
        var grouper = new StubGrouper(Grouped("470"));
        var service = ServiceWith(grouper, drg: "470", weight: 1m, baseRate: 1m);

        await service.RepriceClaimAsync(new RepricingRequest
        {
            FeeScheduleId = Schedule,
            ClaimType = ClaimType.Inpatient,
            PrimaryDiagnosis = "e11.65",
            SecondaryDiagnoses = ["I50.9", "N17.9"],
            InpatientStay = new InpatientStayRequest
            {
                DischargeDate = new DateOnly(2025, 3, 4),
                ProcedureCodes = ["0sr9.019"],
                SecondaryDiagnosisPoa = ["Y", "N"],
                FacilityReportsPoa = true,
                DischargeStatus = "01",
                AgeYears = 71,
                Sex = "F",
                LengthOfStayDays = 4,
                BirthWeightGrams = null
            },
            Lines = [new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447" }]
        });

        var asked = grouper.Asked.Should().NotBeNull().And.Subject.As<MsDrgGroupingRequest>();

        // The decimal point and the case are the grouper tables' business, and
        // this is the one place a claim's codes are put into their shape.
        asked.PrincipalDiagnosis.Code.Should().Be("E1165");
        asked.SecondaryDiagnoses.Select(d => d.Code).Should().Equal("I509", "N179");
        asked.Procedures.Select(p => p.Code).Should().Equal("0SR9019");

        asked.SecondaryDiagnoses.Select(d => d.PresentOnAdmission).Should().Equal("Y", "N");
        asked.PoaReporting.Should().Be(HospitalPoaReporting.NonExempt);
        asked.DischargeDate.Should().Be(new DateOnly(2025, 3, 4));
        asked.DischargeStatus.Should().Be("01");
        asked.AgeYears.Should().Be(71);
        asked.Sex.Should().Be("F");
        asked.LengthOfStayDays.Should().Be(4);
    }

    [Fact]
    public void A_short_poa_list_leaves_the_remaining_diagnoses_without_one()
    {
        var request = new RepricingRequest
        {
            FeeScheduleId = Schedule,
            PrimaryDiagnosis = "E1165",
            SecondaryDiagnoses = ["I509", "N179", "J189"],
            Lines = []
        };

        var built = GrouperInpatientDrgResolver.BuildGroupingRequest(
            request,
            new InpatientStayRequest
            {
                DischargeDate = new DateOnly(2025, 3, 4),
                SecondaryDiagnosisPoa = ["Y"]
            });

        built.SecondaryDiagnoses.Select(d => d.PresentOnAdmission).Should().Equal("Y", null, null);
    }

    [Theory]
    [InlineData(true, HospitalPoaReporting.NonExempt)]
    [InlineData(false, HospitalPoaReporting.Exempt)]
    [InlineData(null, HospitalPoaReporting.Unknown)]
    public void Whether_the_facility_reports_poa_has_three_states_not_two(
        bool? reports, HospitalPoaReporting expected)
    {
        var built = GrouperInpatientDrgResolver.BuildGroupingRequest(
            new RepricingRequest { FeeScheduleId = Schedule, PrimaryDiagnosis = "E1165", Lines = [] },
            new InpatientStayRequest
            {
                DischargeDate = new DateOnly(2025, 3, 4),
                FacilityReportsPoa = reports
            });

        built.PoaReporting.Should().Be(expected);
    }

    // ─────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────

    private static InpatientStayRequest Stay() => new()
    {
        DischargeDate = new DateOnly(2025, 3, 4),
        ProcedureCodes = ["0SR9019"]
    };

    private static RepricingRequest Request(string? drgCode, InpatientStayRequest? stay = null) => new()
    {
        FeeScheduleId = Schedule,
        ClaimType = ClaimType.Inpatient,
        PrimaryDiagnosis = "E1165",
        DrgCode = drgCode,
        InpatientStay = stay,
        Lines = [new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 }]
    };

    private static FeeScheduleInfo Info(string id, FeeScheduleType type) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Version = "2025.1",
        EffectiveDate = new DateOnly(2025, 1, 1),
        CodeCount = 1,
        LastUpdated = DateTimeOffset.UtcNow
    };

    private RepricingService ServiceWith(IMsDrgGrouper grouper, string drg, decimal weight, decimal baseRate)
    {
        var repo = new Mock<IFeeScheduleRepository>();
        repo.Setup(r => r.GetScheduleInfoAsync(Schedule))
            .ReturnsAsync(Info(Schedule, FeeScheduleType.MedicareDrg));
        repo.Setup(r => r.LookupDrgAsync(Schedule, drg))
            .ReturnsAsync(new FeeScheduleEntry
            {
                FeeScheduleId = Schedule,
                ProcedureCode = drg,
                DrgWeight = weight,
                DrgBaseRate = baseRate
            });

        return Service(repo, grouper);
    }

    private static RepricingService Service(Mock<IFeeScheduleRepository> repo, IMsDrgGrouper grouper) =>
        new(repo.Object,
            new CloudHealthOffice.PricingApi.Services.Engine.LegacyEntryScheduleSource(repo.Object),
            new GrouperInpatientDrgResolver(
                grouper, NullLogger<GrouperInpatientDrgResolver>.Instance),
            NullLogger<RepricingService>.Instance,
            NullLoggerFactory.Instance);
}
