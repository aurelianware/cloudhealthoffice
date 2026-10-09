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

    [Theory]
    [InlineData("11", false)]
    [InlineData("12", false)]
    [InlineData("02", false)]
    [InlineData("10", false)]
    [InlineData("21", true)]
    [InlineData("22", true)]
    [InlineData(null, false)]
    public void IsFacilityPlaceOfService_MatchesEngineRule(string? pos, bool expected)
        => Assert.Equal(expected, RateResolutionService.IsFacilityPlaceOfService(pos));

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
        decimal units = 1m) => new()
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
    };
}
