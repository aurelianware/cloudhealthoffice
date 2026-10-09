using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// Contract reimbursement terms added for the unified contract pricing work
/// (ADR 016): lesser-of-billed, flat facility prices, assistant surgeon
/// modifiers 81/82 and cent rounding of allowed amounts.
/// </summary>
public class ContractTermsTests
{
    private const string Tenant = "test-tenant";
    private const string PlanId = "plan-001";
    private const string ProviderNpi = "1234567890";

    // ═══════════════════════════════════════════════════════════════════
    // LESSER-OF-BILLED
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LesserOf_DefaultOff_PaysContractRateAboveBilled()
    {
        var engine = Engine(Commercial(("99213", 150m)), lesserOf: null);

        var result = await engine.ResolveAsync(Request("99213", billed: 100m));

        Assert.Equal(150m, result.AllowedAmount);
        Assert.False(result.LesserOfBilledApplied);
        Assert.Empty(result.Adjustments);
    }

    [Fact]
    public async Task LesserOf_ContractFlagFalse_PaysContractRateAboveBilled()
    {
        var engine = Engine(Commercial(("99213", 150m)), lesserOf: false);

        var result = await engine.ResolveAsync(Request("99213", billed: 100m));

        Assert.Equal(150m, result.AllowedAmount);
        Assert.False(result.LesserOfBilledApplied);
    }

    [Fact]
    public async Task LesserOf_On_BilledBelowRate_AllowsBilled()
    {
        var engine = Engine(Commercial(("99213", 150m)), lesserOf: true);

        var result = await engine.ResolveAsync(Request("99213", billed: 100m));

        Assert.Equal(100m, result.AllowedAmount);
        Assert.True(result.LesserOfBilledApplied);
        var adjustment = Assert.Single(result.Adjustments);
        Assert.Equal(-50m, adjustment.AdjustmentAmount);
        Assert.Contains("Lesser of", adjustment.Description);
        Assert.Equal(0m, result.ContractualAdjustment);
    }

    [Fact]
    public async Task LesserOf_On_BilledAboveRate_AllowsRate()
    {
        var engine = Engine(Commercial(("99213", 150m)), lesserOf: true);

        var result = await engine.ResolveAsync(Request("99213", billed: 400m));

        Assert.Equal(150m, result.AllowedAmount);
        Assert.False(result.LesserOfBilledApplied);
        Assert.Empty(result.Adjustments);
    }

    [Fact]
    public async Task LesserOf_On_ComparesAfterUnitsAndModifiers()
    {
        // 3 units × $150 × 150% bilateral = $675, billed $600 → $600
        var engine = Engine(Commercial(("27447", 150m)), lesserOf: true);

        var result = await engine.ResolveAsync(
            Request("27447", billed: 600m, units: 3m, modifiers: ["50"]));

        Assert.Equal(600m, result.AllowedAmount);
        Assert.True(result.LesserOfBilledApplied);
    }

    [Fact]
    public async Task LesserOf_On_Batch_AppliedAfterMultipleProcedureReduction()
    {
        // 27447 $1500 (rank 1, 100%), 29881 $800 (rank 2 → $400).
        // Billed: 27447 $1000 → $1000; 29881 $450 → $400 (reduced rate is lower).
        var schedule = Commercial(("27447", 1500m), ("29881", 800m));
        foreach (var line in schedule.Lines)
            line.MultipleProcedureIndicator = MultipleProcedureIndicator.StandardSurgery;
        var engine = Engine(schedule, lesserOf: true);

        var results = await engine.ResolveBatchAsync(
        [
            Request("27447", billed: 1000m, lineNumber: 1, totalLines: 2),
            Request("29881", billed: 450m, lineNumber: 2, totalLines: 2),
        ]);

        Assert.Equal(1000m, results.LineResults[0].AllowedAmount);
        Assert.True(results.LineResults[0].LesserOfBilledApplied);
        Assert.Equal(400m, results.LineResults[1].AllowedAmount);
        Assert.False(results.LineResults[1].LesserOfBilledApplied);
    }

    [Fact]
    public async Task LesserOf_On_DrgCaseRate_ComparedWithStayTotalBilled()
    {
        // Case rate $10,000; stay billed $6,000 + $2,000 = $8,000 → $8,000,
        // allocated 75% / 25% by billed charges.
        var engine = Engine(Drg("470", 10_000m), lesserOf: true);

        var results = await engine.ResolveBatchAsync(
        [
            Request("", billed: 6000m, drgCode: "470", lineNumber: 1, totalLines: 2, revenueCode: "0120"),
            Request("", billed: 2000m, drgCode: "470", lineNumber: 2, totalLines: 2, revenueCode: "0250"),
        ]);

        Assert.Equal(8000m, results.TotalAllowedAmount);
        Assert.Equal(6000m, results.LineResults[0].AllowedAmount);
        Assert.Equal(2000m, results.LineResults[1].AllowedAmount);
        Assert.All(results.LineResults, r => Assert.True(r.LesserOfBilledApplied));
    }

    [Fact]
    public async Task LesserOf_On_DrgCaseRate_BelowStayBilled_NotApplied()
    {
        // A line's share is never compared with its own charge: line 2 bills $10
        // but the stay bills $20,010, above the $10,000 case rate.
        var engine = Engine(Drg("470", 10_000m), lesserOf: true);

        var results = await engine.ResolveBatchAsync(
        [
            Request("", billed: 20_000m, drgCode: "470", lineNumber: 1, totalLines: 2, revenueCode: "0120"),
            Request("", billed: 10m, drgCode: "470", lineNumber: 2, totalLines: 2, revenueCode: "0250"),
        ]);

        Assert.Equal(10_000m, results.TotalAllowedAmount);
        Assert.All(results.LineResults, r => Assert.False(r.LesserOfBilledApplied));
    }

    [Fact]
    public async Task LesserOf_On_SingleDrgLine_ComparedWithLineBilled()
    {
        var engine = Engine(Drg("470", 10_000m), lesserOf: true);

        var results = await engine.ResolveBatchAsync(
        [
            Request("", billed: 7500m, drgCode: "470", revenueCode: "0120"),
        ]);

        Assert.Equal(7500m, results.TotalAllowedAmount);
        Assert.True(results.LineResults[0].LesserOfBilledApplied);
    }

    [Fact]
    public async Task LesserOf_On_DrgCaseRate_OneStayLineAmongOrdinaryLines()
    {
        // One per-stay line plus a carve-out priced from another schedule: the
        // stay comparison uses the per-stay line's charge only.
        var drg = Drg("470", 10_000m);
        var repo = new InMemoryFeeScheduleRepo(drg);
        var carveOut = Commercial(("J1885", 80m));
        carveOut.Id = "carve-out";
        repo.AddSchedule(carveOut);
        var contract = Contract(drg.Id, lesserOf: true);
        contract.ContractLines.Add(new ProviderContractLine
        {
            ProcedureCodeFrom = "J1885", ProcedureCodeTo = "J1885", FeeScheduleId = carveOut.Id,
        });
        var engine = new RateResolutionService(repo, new FixedContractRepo(contract),
            NullLogger<RateResolutionService>.Instance);

        var results = await engine.ResolveBatchAsync(
        [
            Request("", billed: 9000m, drgCode: "470", lineNumber: 1, totalLines: 2, revenueCode: "0120"),
            Request("J1885", billed: 50m, drgCode: "470", lineNumber: 2, totalLines: 2),
        ]);

        Assert.Equal(9000m, results.LineResults[0].AllowedAmount);
        Assert.Equal(50m, results.LineResults[1].AllowedAmount);
        Assert.All(results.LineResults, r => Assert.True(r.LesserOfBilledApplied));
    }

    [Fact]
    public async Task LesserOf_On_DrgStay_SubCentBilled_EveryLineInWholeCents()
    {
        // Sub-cent billed charges (e.g. from an upstream feed): the stay total and
        // every allocated share stay in whole cents and still sum to the stay amount.
        var engine = Engine(Drg("470", 10_000m), lesserOf: true);

        var results = await engine.ResolveBatchAsync(
        [
            Request("", billed: 1000.004m, drgCode: "470", lineNumber: 1, totalLines: 3, revenueCode: "0120"),
            Request("", billed: 2000.006m, drgCode: "470", lineNumber: 2, totalLines: 3, revenueCode: "0250"),
            Request("", billed: 333.333m, drgCode: "470", lineNumber: 3, totalLines: 3, revenueCode: "0300"),
        ]);

        Assert.Equal(3333.34m, results.TotalAllowedAmount); // 1000.00 + 2000.01 + 333.33
        Assert.All(results.LineResults, r =>
        {
            Assert.Equal(Math.Round(r.AllowedAmount, 2), r.AllowedAmount);
            Assert.True(r.LesserOfBilledApplied);
        });
    }

    [Fact]
    public async Task LesserOf_On_SubCentBilled_AllowedInWholeCents()
    {
        var engine = Engine(Commercial(("99213", 150m)), lesserOf: true);

        var result = await engine.ResolveAsync(Request("99213", billed: 99.996m));

        Assert.Equal(100.00m, result.AllowedAmount);
        Assert.True(result.LesserOfBilledApplied);
    }

    [Fact]
    public async Task LesserOf_On_BilledChargeFallback_Untouched()
    {
        // No rate line: the engine falls back to billed charges, which lesser-of leaves alone.
        var engine = Engine(Commercial(("99213", 150m)), lesserOf: true);

        var result = await engine.ResolveAsync(Request("99999", billed: 75m));

        Assert.Equal(RateSource.BilledCharges, result.RateSource);
        Assert.Equal(75m, result.AllowedAmount);
        Assert.False(result.LesserOfBilledApplied);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FLAT FACILITY PRICE
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("11", 110.00)]
    [InlineData("22", 75.00)]
    [InlineData("21", 75.00)]
    public async Task FacilityRate_SelectedByPlaceOfService(string pos, double expected)
    {
        var schedule = new FeeSchedule
        {
            Id = "mpfs-flat", TenantId = Tenant, Name = "MPFS flat",
            Type = FeeScheduleType.MedicareMpfs,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 110m, FacilityRate = 75m }],
        };
        var engine = Engine(schedule, lesserOf: null);

        var result = await engine.ResolveAsync(Request("99213", pos: pos));

        Assert.Equal((decimal)expected, result.AllowedAmount);
        Assert.Equal((decimal)expected, result.BaseAmount);
    }

    [Fact]
    public async Task FacilityRate_Unset_SameRateEverywhere()
    {
        var engine = Engine(Commercial(("99213", 110m)), lesserOf: null);

        var result = await engine.ResolveAsync(Request("99213", pos: "22"));

        Assert.Equal(110m, result.AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FACILITY / NON-FACILITY PLACE OF SERVICE (CMS Pub. 100-04 Ch. 12 §20.4.2)
    // ═══════════════════════════════════════════════════════════════════

    private static FeeSchedule FlatWithFacilityPrice() => new()
    {
        Id = "mpfs-flat", TenantId = Tenant, Name = "MPFS flat",
        Type = FeeScheduleType.MedicareMpfs,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 110m, FacilityRate = 75m }],
    };

    private static FeeSchedule RvuSchedule() => new()
    {
        Id = "mpfs-rvu", TenantId = Tenant, Name = "MPFS RVU",
        Type = FeeScheduleType.MedicareMpfs,
        EffectiveDate = new DateTime(2026, 1, 1),
        ConversionFactor = 33m,
        Lines =
        [
            new FeeScheduleLine
            {
                ProcedureCode = "99213", RateType = FeeScheduleRateType.Rvu,
                WorkRvu = 1.30m, PeRvu = 1.59m, PeRvuFacility = 0.83m, MpRvu = 0.09m,
            },
        ],
    };

    // 99213 at CF 33: non-facility (1.30 + 1.59 + 0.09) × 33 = 98.34; facility (1.30 + 0.83 + 0.09) × 33 = 73.26.
    private const decimal RvuNonFacility = 98.34m;
    private const decimal RvuFacility = 73.26m;

    /// <summary>
    /// Every code in the CMS Place of Service Code Set (plus blank, unassigned and
    /// malformed values) with the rate setting §20.4.2 gives it.
    /// </summary>
    public static TheoryData<string?, bool> CmsPlaceOfServiceTable()
    {
        var facility = new HashSet<string>
        {
            "02", "19", "21", "22", "23", "24", "26", "31", "34", "41", "42", "51", "52", "53", "56", "61",
        };
        string[] assigned =
        [
            "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12", "13", "14", "15", "16",
            "17", "18", "19", "20", "21", "22", "23", "24", "25", "26", "27", "31", "32", "33", "34", "41",
            "42", "49", "50", "51", "52", "53", "54", "55", "56", "57", "58", "60", "61", "62", "65", "66",
            "71", "72", "81", "99",
        ];
        var data = new TheoryData<string?, bool>();
        foreach (var pos in assigned) data.Add(pos, facility.Contains(pos));
        // Blank, unassigned and malformed codes: non-facility.
        foreach (var pos in new string?[] { null, "", "  ", "00", "28", "40", "98", "XX", "2", "021" })
            data.Add(pos, false);
        // Surrounding whitespace is ignored.
        data.Add(" 21 ", true);
        return data;
    }

    [Theory]
    [MemberData(nameof(CmsPlaceOfServiceTable))]
    public void FacilityPlaceOfService_MatchesCmsTable(string? pos, bool expected)
    {
        Assert.Equal(expected, FacilityPlaceOfService.IsFacility(pos));
        Assert.Equal(expected, RateResolutionService.IsFacilityPlaceOfService(pos));
    }

    [Theory]
    [MemberData(nameof(CmsPlaceOfServiceTable))]
    public async Task FacilityPlaceOfService_DrivesFlatAndRvuRates(string? pos, bool facility)
    {
        var flat = await Engine(FlatWithFacilityPrice(), lesserOf: null).ResolveAsync(Request("99213", pos: pos!));
        var rvu = await Engine(RvuSchedule(), lesserOf: null).ResolveAsync(Request("99213", pos: pos!));

        Assert.Equal(facility ? 75m : 110m, flat.AllowedAmount);
        Assert.Equal(facility ? RvuFacility : RvuNonFacility, rvu.AllowedAmount);
    }

    [Fact]
    public void FacilityPlaceOfService_IsExactlyTheCmsFacilityList()
        => Assert.Equal(
            new[] { "02", "19", "21", "22", "23", "24", "26", "31", "34", "41", "42", "51", "52", "53", "56", "61" },
            FacilityPlaceOfService.Codes.Order(StringComparer.Ordinal));

    // One case per CMS POS whose classification changed. The previous rule made
    // every POS a facility setting except 11, 12, 02 and 10.

    [Fact]
    public async Task Pos02_TelehealthOtherThanHome_NowFacility()
        => await AssertSetting("02", facility: true);

    [Fact]
    public async Task Pos20_UrgentCare_NowNonFacility()
        => await AssertSetting("20", facility: false);

    [Fact]
    public async Task Pos49_IndependentClinic_NowNonFacility()
        => await AssertSetting("49", facility: false);

    [Fact]
    public async Task Pos81_IndependentLaboratory_NowNonFacility()
        => await AssertSetting("81", facility: false);

    [Theory]
    [InlineData("01")] // Pharmacy
    [InlineData("03")] // School
    [InlineData("04")] // Homeless shelter
    [InlineData("05")] // IHS free-standing facility
    [InlineData("06")] // IHS provider-based facility
    [InlineData("07")] // Tribal 638 free-standing facility
    [InlineData("08")] // Tribal 638 provider-based facility
    [InlineData("09")] // Prison / correctional facility
    [InlineData("13")] // Assisted living facility
    [InlineData("14")] // Group home
    [InlineData("15")] // Mobile unit
    [InlineData("16")] // Temporary lodging
    [InlineData("17")] // Walk-in retail health clinic
    [InlineData("18")] // Place of employment / worksite
    [InlineData("20")] // Urgent care facility
    [InlineData("25")] // Birthing center
    [InlineData("27")] // Outreach site / street
    [InlineData("32")] // Nursing facility (and SNF, Part B resident)
    [InlineData("33")] // Custodial care facility
    [InlineData("49")] // Independent clinic
    [InlineData("50")] // Federally qualified health center
    [InlineData("54")] // Intermediate care facility / individuals with intellectual disabilities
    [InlineData("55")] // Residential substance abuse treatment facility
    [InlineData("57")] // Non-residential substance abuse treatment facility
    [InlineData("58")] // Non-residential opioid treatment facility
    [InlineData("60")] // Mass immunization center
    [InlineData("62")] // Comprehensive outpatient rehabilitation facility
    [InlineData("65")] // End-stage renal disease treatment facility
    [InlineData("66")] // Programs of All-Inclusive Care for the Elderly (PACE) center
    [InlineData("71")] // State or local public health clinic
    [InlineData("72")] // Rural health clinic
    [InlineData("81")] // Independent laboratory
    [InlineData("99")] // Other place of service
    public async Task PreviouslyFacility_NowNonFacility(string pos)
        => await AssertSetting(pos, facility: false);

    [Theory]
    [InlineData("10")] // Telehealth in patient's home: non-facility (CY 2024 rule), every date of service
    [InlineData("11")]
    [InlineData("12")]
    public async Task UnchangedNonFacility(string pos)
        => await AssertSetting(pos, facility: false);

    [Theory]
    [InlineData("20")]
    [InlineData("49")]
    [InlineData("81")]
    [InlineData("21")]
    public async Task NoFacilityRate_SingleRateEverywhere(string pos)
    {
        var engine = Engine(Commercial(("99213", 110m)), lesserOf: null);

        var result = await engine.ResolveAsync(Request("99213", pos: pos));

        Assert.Equal(110m, result.AllowedAmount);
    }

    [Theory]
    [InlineData("13", "131")] // Hospital outpatient: facility type, not read as POS 13
    [InlineData("11", "111")] // Hospital inpatient: not read as POS 11 (office)
    [InlineData("83", "831")] // Ambulatory surgery center
    public async Task InstitutionalLine_AlwaysFacility(string facilityTypeCode, string billType)
    {
        var engine = Engine(FlatWithFacilityPrice(), lesserOf: null);

        var result = await engine.ResolveAsync(Request("99213", pos: facilityTypeCode, billType: billType));

        Assert.Equal(75m, result.AllowedAmount);
    }

    private static async Task AssertSetting(string pos, bool facility)
    {
        Assert.Equal(facility, FacilityPlaceOfService.IsFacility(pos));

        var flat = await Engine(FlatWithFacilityPrice(), lesserOf: null).ResolveAsync(Request("99213", pos: pos));
        Assert.Equal(facility ? 75m : 110m, flat.AllowedAmount);

        var rvu = await Engine(RvuSchedule(), lesserOf: null).ResolveAsync(Request("99213", pos: pos));
        Assert.Equal(facility ? RvuFacility : RvuNonFacility, rvu.AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ASSISTANT SURGEON 81 / 82, ROUNDING
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("80")]
    [InlineData("81")]
    [InlineData("82")]
    public async Task AssistantSurgeonModifiers_PayAt16Percent(string modifier)
    {
        var engine = Engine(Commercial(("27447", 1000m)), lesserOf: null);

        var result = await engine.ResolveAsync(Request("27447", billed: 5000m, modifiers: [modifier]));

        Assert.Equal(160m, result.AllowedAmount);
        Assert.Equal(modifier, Assert.Single(result.Adjustments).Modifier);
    }

    [Fact]
    public async Task AssistantSurgeon_TwoModifiers_AppliedOnce()
    {
        var engine = Engine(Commercial(("27447", 1000m)), lesserOf: null);

        var result = await engine.ResolveAsync(Request("27447", billed: 5000m, modifiers: ["80", "82"]));

        Assert.Equal(160m, result.AllowedAmount);
    }

    [Fact]
    public async Task AllowedAmount_RoundedToCents()
    {
        // $110.37 × 16% = $17.6592 → $17.66
        var engine = Engine(Commercial(("27447", 110.37m)), lesserOf: null);

        var result = await engine.ResolveAsync(Request("27447", billed: 5000m, modifiers: ["80"]));

        Assert.Equal(17.66m, result.AllowedAmount);
        Assert.Equal(110.37m, result.BaseAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static FeeSchedule Commercial(params (string code, decimal rate)[] lines) => new()
    {
        Id = "comm-test", TenantId = Tenant, Name = "Commercial Test",
        Type = FeeScheduleType.Commercial,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = lines.Select(l => new FeeScheduleLine { ProcedureCode = l.code, Rate = l.rate }).ToList(),
    };

    private static FeeSchedule Drg(string drgCode, decimal caseRate) => new()
    {
        Id = "drg-test", TenantId = Tenant, Name = "DRG Test",
        Type = FeeScheduleType.Drg,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = [new FeeScheduleLine { ProcedureCode = drgCode, Rate = caseRate }],
    };

    private static ProviderContract Contract(string feeScheduleId, bool lesserOf) => new()
    {
        Id = "contract-test", TenantId = Tenant, ProviderNpi = ProviderNpi, PlanId = PlanId,
        NetworkStatus = NetworkStatus.InNetwork, FeeScheduleId = feeScheduleId,
        EffectiveDate = new DateTime(2026, 1, 1), LesserOfBilledCharges = lesserOf,
    };

    /// <param name="lesserOf">null = a contract stored before the provision existed (property unset).</param>
    private static RateResolutionService Engine(FeeSchedule schedule, bool? lesserOf)
    {
        var contract = lesserOf is null
            ? new ProviderContract
            {
                Id = "contract-test", TenantId = Tenant, ProviderNpi = ProviderNpi, PlanId = PlanId,
                NetworkStatus = NetworkStatus.InNetwork, FeeScheduleId = schedule.Id,
                EffectiveDate = new DateTime(2026, 1, 1),
            }
            : Contract(schedule.Id, lesserOf.Value);
        return new RateResolutionService(new InMemoryFeeScheduleRepo(schedule), new FixedContractRepo(contract),
            NullLogger<RateResolutionService>.Instance);
    }

    private static PricingRequest Request(
        string procedureCode,
        string pos = "11",
        decimal billed = 200m,
        string? drgCode = null,
        int lineNumber = 1,
        int totalLines = 1,
        List<string>? modifiers = null,
        string? revenueCode = null,
        decimal units = 1m,
        string? billType = null) => new()
    {
        TenantId = Tenant,
        ProcedureCode = procedureCode,
        Modifiers = modifiers ?? [],
        ProviderNpi = ProviderNpi,
        PlaceOfServiceCode = pos,
        ServiceDate = new DateTime(2026, 3, 8),
        PlanId = PlanId,
        BilledAmount = billed,
        Units = units,
        LineNumber = lineNumber,
        TotalLineCount = totalLines,
        DrgCode = drgCode,
        RevenueCode = revenueCode,
        BillType = billType,
    };
}
