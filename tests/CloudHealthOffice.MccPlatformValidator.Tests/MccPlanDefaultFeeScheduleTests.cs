using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccPlanDefaultFeeScheduleTests
{
    /// <summary>The validator's request serializer options (Program.cs).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] InstitutionalSubTypes =
        ["inpatient", "outpatient", "emergency", "observation", "stoploss", "skillednursing"];

    [Fact]
    public void Payload_PricesEveryRevenueCodeTheInstitutionalGeneratorEmits()
    {
        var payload = MccPlanDefaultFeeSchedule.BuildPayload();
        var revenueRates = payload.Lines
            .Where(l => l.RevenueCode is not null)
            .ToDictionary(l => l.RevenueCode!, l => l);

        var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider());
        var random = new Random(42);
        var emitted = new HashSet<string>();
        for (var i = 0; i < 1_200; i++)
        {
            var claim = generator.Generate(i, InstitutionalSubTypes[i % InstitutionalSubTypes.Length], random);
            foreach (var line in claim.Lines)
                emitted.Add(line.RevenueCode!);
        }

        Assert.NotEmpty(emitted);
        foreach (var code in emitted)
        {
            Assert.True(revenueRates.TryGetValue(code, out var line), $"no plan-default line for revenue code {code}");
            Assert.Equal(string.Empty, line!.ProcedureCode);
            Assert.True(line.Rate > 0m);
        }
    }

    [Fact]
    public void Payload_KeepsTheGeneratorsProcedureCodeCoverage_WithNoDuplicateKeys()
    {
        var payload = MccPlanDefaultFeeSchedule.BuildPayload();

        var procedureLines = payload.Lines.Where(l => l.RevenueCode is null).ToList();
        Assert.Contains(procedureLines, l => l.ProcedureCode == "99213" && l.Rate == 85m);
        Assert.All(procedureLines, l => Assert.False(string.IsNullOrWhiteSpace(l.ProcedureCode)));

        var keys = payload.Lines.Select(l => (l.ProcedureCode, l.ProcedureCode.Length == 0 ? l.RevenueCode : null, l.Modifier));
        Assert.Equal(payload.Lines.Count, keys.Distinct().Count());
        Assert.All(payload.Lines, l => Assert.True(l.Rate >= 0m));
    }

    [Fact]
    public void Payload_IsDeterministic_SoReseedingWritesTheSameSchedule()
    {
        var first = JsonSerializer.Serialize(MccPlanDefaultFeeSchedule.BuildPayload(), Json);
        var second = JsonSerializer.Serialize(MccPlanDefaultFeeSchedule.BuildPayload(), Json);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Path_IsKeyedByPlan()
    {
        var plan = Guid.Parse("6f1c3c2e-8f8f-4a52-9a7a-0f1d2b3c4d5e");

        Assert.Equal(
            "/api/v1/plans/6f1c3c2e-8f8f-4a52-9a7a-0f1d2b3c4d5e/default-fee-schedule",
            MccPlanDefaultFeeSchedule.Path(plan));
        Assert.Equal(MccPlanDefaultFeeSchedule.Path(plan), MccPlanDefaultFeeSchedule.Path(plan));
        Assert.NotEqual(MccPlanDefaultFeeSchedule.Path(plan), MccPlanDefaultFeeSchedule.Path(Guid.NewGuid()));
    }

    [Fact]
    public void Payload_SerializesToTheBenefitPlanServiceRequestShape()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(MccPlanDefaultFeeSchedule.BuildPayload(), Json));
        var root = doc.RootElement;

        Assert.Equal("MCC Plan Default Fee Schedule", root.GetProperty("name").GetString());
        Assert.Equal(new DateTime(2024, 1, 1), root.GetProperty("effectiveDate").GetDateTime().Date);

        var revenueLine = root.GetProperty("lines").EnumerateArray()
            .Single(l => l.TryGetProperty("revenueCode", out var rc) && rc.GetString() == "0120");
        Assert.Equal(string.Empty, revenueLine.GetProperty("procedureCode").GetString());
        Assert.Equal(1800m, revenueLine.GetProperty("rate").GetDecimal());
        Assert.Equal(9, revenueLine.GetProperty("multipleProcedureIndicator").GetInt32());

        var cptLine = root.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("procedureCode").GetString() == "99213");
        Assert.False(cptLine.TryGetProperty("revenueCode", out _));
    }

    [Fact]
    public void SeededRates_MatchTheGeneratorsExpectedInstitutionalAllowed()
    {
        var rates = MccPlanDefaultFeeSchedule.BuildPayload().Lines
            .Where(l => l.RevenueCode is not null)
            .ToDictionary(l => l.RevenueCode!, l => l.Rate);
        var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider());
        var random = new Random(3);

        for (var i = 0; i < 120; i++)
        {
            var claim = generator.Generate(i, InstitutionalSubTypes[i % InstitutionalSubTypes.Length], random);
            foreach (var line in claim.Lines)
            {
                var expected = claim.ExpectedOutcome!.LineOutcomes.Single(o => o.LineNumber == line.LineNumber);
                Assert.Equal(rates[line.RevenueCode!] * line.Units, expected.AllowedAmount);
                Assert.True(expected.AllowedAmount <= MccPlanDefaultFeeSchedule.LineChargeTotal(line));
            }
        }
    }

    [Theory]
    [InlineData(2500, 4, 10000)] // per-diem line: charge per day × days
    [InlineData(300, 1, 300)]
    [InlineData(180, 0, 180)]    // units are submitted as at least 1
    public void LineChargeTotal_IsChargeTimesSubmittedUnits(decimal charge, decimal units, decimal expected)
    {
        var line = new ClaimLine { ChargeAmount = charge, Units = units };

        Assert.Equal(expected, MccPlanDefaultFeeSchedule.LineChargeTotal(line));
    }

    [Fact]
    public void LineChargeTotals_SumToTheClaimTotal()
    {
        var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider());
        var claim = generator.Generate(1, "inpatient", new Random(5));

        Assert.Equal(claim.TotalCharges, claim.Lines.Sum(MccPlanDefaultFeeSchedule.LineChargeTotal));
        Assert.Equal(
            SyntheticFeeScheduleGenerator.PlanDefaultFeeScheduleId,
            SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule().FeeScheduleId);
    }
}
