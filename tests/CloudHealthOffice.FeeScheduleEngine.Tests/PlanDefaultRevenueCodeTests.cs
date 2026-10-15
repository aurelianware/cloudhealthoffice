using System.Text.Json;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// A provider with no contract on an institutional (837I) claim whose lines
/// carry only revenue codes is priced from the plan's default schedule: the
/// schedule line keyed by revenue code with a blank procedure code. This is
/// the shape the MCC validator seeds for its validation plan.
/// </summary>
public class PlanDefaultRevenueCodeTests
{
    private const string Tenant = "test-tenant";
    private const string PlanId = "6f1c3c2e-8f8f-4a52-9a7a-0f1d2b3c4d5e";

    private static FeeSchedule PlanDefaultSchedule() => new()
    {
        Id = $"{Tenant}:plan-default:{PlanId}",
        TenantId = Tenant,
        Name = "MCC Validation Plan Default",
        Type = FeeScheduleType.Custom,
        DefaultForPlanId = PlanId,
        EffectiveDate = new DateTime(2024, 1, 1),
        Lines =
        [
            new FeeScheduleLine { ProcedureCode = "99213", Rate = 85m, RateType = FeeScheduleRateType.FlatRate },
            new FeeScheduleLine { ProcedureCode = string.Empty, RevenueCode = "0120", Rate = 1800m, RateType = FeeScheduleRateType.FlatRate },
            new FeeScheduleLine { ProcedureCode = string.Empty, RevenueCode = "0300", Rate = 120m, RateType = FeeScheduleRateType.FlatRate },
        ],
    };

    private static RateResolutionService Engine(FeeSchedule schedule)
        => new(
            new InMemoryFeeScheduleRepo(schedule),
            new InMemoryProviderContractRepo(feeScheduleId: null), // no contract → plan default
            NullLogger<RateResolutionService>.Instance);

    private static PricingRequest InstitutionalLine(
        string procedureCode, string revenueCode, decimal units, decimal billed, int lineNumber = 1, int totalLines = 1)
        => new()
        {
            TenantId = Tenant,
            ProcedureCode = procedureCode,
            RevenueCode = revenueCode,
            Units = units,
            BilledAmount = billed,
            ProviderNpi = "1234567893",
            PlanId = PlanId,
            PlaceOfServiceCode = "11", // CLM05-1 facility type on an 837I, not a CMS POS
            BillType = "0111",
            IsInstitutional = true,
            ServiceDate = new DateTime(2024, 3, 8),
            LineNumber = lineNumber,
            TotalLineCount = totalLines,
        };

    [Theory]
    [InlineData("")]      // revenue-code-only 837I line
    [InlineData("0120")]  // benchmark generator shape: ProcedureCode echoes the revenue code
    public async Task RevenueCodeOnlyLine_NoContract_ResolvesPlanDefaultBlankProcedureLine(string procedureCode)
    {
        var engine = Engine(PlanDefaultSchedule());

        // Room and board per day: 3 days × 1800.
        var result = await engine.ResolveAsync(InstitutionalLine(procedureCode, "0120", units: 3, billed: 7500m));

        Assert.Equal(RateSource.PlanDefault, result.RateSource);
        Assert.Equal(FeeScheduleType.Custom, result.FeeScheduleType);
        Assert.Equal(5400m, result.AllowedAmount);
        Assert.Equal($"{Tenant}:plan-default:{PlanId}", result.FeeScheduleId);
        Assert.Equal(NetworkStatus.Unknown, result.NetworkStatus);
    }

    [Fact]
    public async Task RevenueCodeOnlyLines_Batch_EveryLinePricedFromPlanDefault()
    {
        var engine = Engine(PlanDefaultSchedule());

        var result = await engine.ResolveBatchAsync(
        [
            InstitutionalLine("0120", "0120", units: 2, billed: 6000m, lineNumber: 1, totalLines: 2),
            InstitutionalLine("0300", "0300", units: 1, billed: 400m, lineNumber: 2, totalLines: 2),
        ]);

        Assert.All(result.LineResults, r => Assert.Equal(RateSource.PlanDefault, r.RateSource));
        Assert.Equal(new[] { 3600m, 120m }, result.LineResults.OrderBy(r => r.LineNumber).Select(r => r.AllowedAmount));
    }

    [Fact]
    public async Task RevenueCodeWithoutPlanDefaultLine_StillFallsBackToBilledCharges()
    {
        var engine = Engine(PlanDefaultSchedule());

        var result = await engine.ResolveAsync(InstitutionalLine("0450", "0450", units: 1, billed: 900m));

        Assert.Equal(RateSource.BilledCharges, result.RateSource);
    }

    [Fact]
    public void DefaultForPlanId_StoredAsDefaultForPlanId_InBsonAndJson()
    {
        var schedule = PlanDefaultSchedule();

        var bson = schedule.ToBsonDocument();
        Assert.Equal(PlanId, bson["defaultForPlanId"].AsString);
        var roundTripped = BsonSerializer.Deserialize<FeeSchedule>(bson);
        Assert.Equal(PlanId, roundTripped.DefaultForPlanId);

        var json = JsonSerializer.Serialize(schedule);
        Assert.Contains("\"defaultForPlanId\":\"" + PlanId + "\"", json);
    }

    [Fact]
    public void DefaultForPlanId_Null_NotWrittenToBson()
    {
        var schedule = PlanDefaultSchedule();
        schedule.DefaultForPlanId = null;

        Assert.False(schedule.ToBsonDocument().Contains("defaultForPlanId"));
    }
}
