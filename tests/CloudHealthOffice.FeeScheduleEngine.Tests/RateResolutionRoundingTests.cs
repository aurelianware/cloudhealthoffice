using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// Allowed amounts are money and are rounded to the cent half away from zero
/// (MoneyRounding.ToCents). Each case lands exactly on a half cent whose cent
/// below is even, where banker's rounding (the old Math.Round(x, 2)) rounded down.
/// </summary>
public class RateResolutionRoundingTests
{
    private const string Tenant = "test-tenant";
    private static readonly DateTime Effective = new(2026, 1, 1);

    [Fact]
    public async Task FlatRate_AtHalfCent_RoundsAwayFromZero()
    {
        // $10.125 → $10.13 (banker's: $10.12)
        var engine = CreateEngine(new FeeSchedule
        {
            Id = "comm-mid", TenantId = Tenant, Name = "Commercial", Type = FeeScheduleType.Commercial,
            EffectiveDate = Effective,
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", Rate = 10.125m }],
        });

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(10.13m, result.AllowedAmount);
    }

    [Fact]
    public async Task FlatRateTimesUnits_AtHalfCent_RoundsAwayFromZero()
    {
        // $20.25 × 0.5 units = $10.125 → $10.13 (banker's: $10.12)
        var engine = CreateEngine(new FeeSchedule
        {
            Id = "comm-units", TenantId = Tenant, Name = "Commercial", Type = FeeScheduleType.Commercial,
            EffectiveDate = Effective,
            Lines = [new FeeScheduleLine { ProcedureCode = "J1100", Rate = 20.25m }],
        });

        var result = await engine.ResolveAsync(Request("J1100", units: 0.5m));

        Assert.Equal(10.13m, result.AllowedAmount);
    }

    [Fact]
    public async Task RvuTimesConversionFactor_AtHalfCent_RoundsAwayFromZero()
    {
        // (1.00 + 0 + 0) × 10.125 = $10.125 → $10.13 (banker's: $10.12)
        var engine = CreateEngine(new FeeSchedule
        {
            Id = "mpfs-mid", TenantId = Tenant, Name = "MPFS", Type = FeeScheduleType.MedicareMpfs,
            EffectiveDate = Effective,
            ConversionFactor = 10.125m,
            WorkGpci = 1.0m, PeGpci = 1.0m, MpGpci = 1.0m,
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "99213", RateType = FeeScheduleRateType.Rvu,
                    WorkRvu = 1.00m, PeRvu = 0m, PeRvuFacility = 0m, MpRvu = 0m,
                },
            ],
        });

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(10.13m, result.AllowedAmount);
    }

    [Fact]
    public async Task DrgBaseRateTimesWeight_AtHalfCent_RoundsAwayFromZero()
    {
        // $6,543.23 × 1.5 = $9,814.845 → $9,814.85 (banker's: $9,814.84)
        var engine = CreateEngine(new FeeSchedule
        {
            Id = "drg-mid", TenantId = Tenant, Name = "DRG", Type = FeeScheduleType.Drg,
            EffectiveDate = Effective,
            DrgBaseRate = 6543.23m,
            Lines = [new FeeScheduleLine { ProcedureCode = "470", Rate = 0m, DrgWeight = 1.5m }],
        });

        var result = await engine.ResolveAsync(Request("99223", drgCode: "470"));

        Assert.Equal(9814.85m, result.AllowedAmount);
    }

    [Fact]
    public async Task MedicaidPercentOfStoredRate_AtHalfCent_RoundsAwayFromZero()
    {
        // 70% of the Medicaid line's $50.35 = $35.245 → $35.25 (banker's: $35.24)
        var engine = CreateEngine(new FeeSchedule
        {
            Id = "medicaid-mid", TenantId = Tenant, Name = "Medicaid", Type = FeeScheduleType.Medicaid,
            EffectiveDate = Effective,
            PercentOfMedicare = 0.70m,
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 50.35m }],
        });

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(35.25m, result.AllowedAmount);
    }

    [Fact]
    public async Task MultipleProcedureReduction_AtHalfCent_RoundsAwayFromZero()
    {
        // Second-ranked surgery at 50% of $100.01 = $50.005 → $50.01 (banker's: $50.00)
        var engine = CreateEngine(new FeeSchedule
        {
            Id = "comm-mppr", TenantId = Tenant, Name = "Commercial", Type = FeeScheduleType.Commercial,
            EffectiveDate = Effective,
            Lines =
            [
                new FeeScheduleLine { ProcedureCode = "27447", Rate = 500m, MultipleProcedureIndicator = MultipleProcedureIndicator.StandardSurgery },
                new FeeScheduleLine { ProcedureCode = "20610", Rate = 100.01m, MultipleProcedureIndicator = MultipleProcedureIndicator.StandardSurgery },
            ],
        });

        var resultSet = await engine.ResolveBatchAsync(
        [
            Request("27447", lineNumber: 1, totalLines: 2, billed: 1000m),
            Request("20610", lineNumber: 2, totalLines: 2, billed: 300m),
        ]);

        Assert.Equal(500m, resultSet.LineResults.Single(r => r.ProcedureCode == "27447").AllowedAmount);
        var reduced = resultSet.LineResults.Single(r => r.ProcedureCode == "20610");
        Assert.Equal(50.01m, reduced.AllowedAmount);
    }

    private static PricingRequest Request(
        string procedureCode,
        decimal billed = 200m,
        string? drgCode = null,
        int lineNumber = 1,
        int totalLines = 1,
        decimal units = 1m) => new()
    {
        TenantId = Tenant,
        ProcedureCode = procedureCode,
        Modifiers = [],
        ProviderNpi = "1234567890",
        PlaceOfServiceCode = "11",
        ServiceDate = new DateTime(2026, 3, 8),
        PlanId = "plan-001",
        BilledAmount = billed,
        Units = units,
        LineNumber = lineNumber,
        TotalLineCount = totalLines,
        DrgCode = drgCode,
    };

    private static RateResolutionService CreateEngine(FeeSchedule schedule)
        => new(
            new InMemoryFeeScheduleRepo(schedule),
            new InMemoryProviderContractRepo(schedule.Id),
            NullLogger<RateResolutionService>.Instance);
}
