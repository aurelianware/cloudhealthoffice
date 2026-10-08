using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

/// <summary>
/// The benchmark seed must carry the CMS multiple procedure indicator on every fee
/// schedule line; the engine only reduces indicator-2 lines and flags lines without one.
/// </summary>
public class FeeScheduleSeedIndicatorTests
{
    private const string Tenant = "mcc-benchmark";

    [Fact]
    public void Generator_AssignsCmsIndicatorToEveryLine()
    {
        var lines = SyntheticFeeScheduleGenerator.Generate(42).SelectMany(fs => fs.Lines).ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.NotNull(l.MultipleProcedureIndicator));

        var medicaid = SyntheticFeeScheduleGenerator.Generate(42).Single(fs => fs.FeeScheduleId == "FS-MEDICAID");
        Assert.Equal(0, medicaid.Lines.Single(l => l.ProcedureCode == "99213").MultipleProcedureIndicator);
        Assert.Equal(2, medicaid.Lines.Single(l => l.ProcedureCode == "27447").MultipleProcedureIndicator);
        Assert.Equal(2, medicaid.Lines.Single(l => l.ProcedureCode == "29881").MultipleProcedureIndicator);
        Assert.Equal(9, medicaid.Lines.Single(l => l.ProcedureCode == "80053").MultipleProcedureIndicator);
        Assert.True(medicaid.Lines.Single(l => l.ProcedureCode == "27447").MultipleProcedureReductionApplies);
        Assert.False(medicaid.Lines.Single(l => l.ProcedureCode == "99213").MultipleProcedureReductionApplies);

        // The out-of-network schedule copies indicators from Medicaid
        var oon = SyntheticFeeScheduleGenerator.Generate(42).Single(fs => fs.FeeScheduleId == "FS-OON");
        Assert.Equal(2, oon.Lines.Single(l => l.ProcedureCode == "27447").MultipleProcedureIndicator);
    }

    [Fact]
    public async Task Seeder_EmitsMultipleProcedureIndicatorOnEveryLine()
    {
        var seeder = new CapturingSeeder();
        await seeder.SeedFeeSchedulesAsync(SyntheticFeeScheduleGenerator.Generate(42));

        var lines = seeder.FeeScheduleDocuments.SelectMany(d => (JArray)d["lines"]!).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal(JTokenType.Integer, l["multipleProcedureIndicator"]?.Type));
        Assert.Equal(2, (int)lines.First(l => (string?)l["procedureCode"] == "27447")["multipleProcedureIndicator"]!);
        Assert.Equal(0, (int)lines.First(l => (string?)l["procedureCode"] == "99213")["multipleProcedureIndicator"]!);
    }

    /// <summary>
    /// End to end: seed document → engine FeeSchedule (Cosmos SDK default Json.NET
    /// deserialization) → batch pricing of a benchmark-style multi-line surgery claim.
    /// Surgeries price 100/50, E&amp;M is untouched, and no missing-indicator warnings.
    /// </summary>
    [Fact]
    public async Task SeededSchedule_MultiLineSurgeryClaim_Prices100_50_WithoutWarnings()
    {
        var seeder = new CapturingSeeder();
        await seeder.SeedFeeSchedulesAsync(SyntheticFeeScheduleGenerator.Generate(42));

        var medicaidDoc = seeder.FeeScheduleDocuments.Single(d => (string?)d["feeScheduleId"] == "FS-MEDICAID");
        var schedule = medicaidDoc.ToObject<FeeSchedule>()!;
        Assert.Equal(MultipleProcedureIndicator.StandardSurgery,
            schedule.Lines.Single(l => l.ProcedureCode == "27447").MultipleProcedureIndicator);

        var engine = new RateResolutionService(
            new SingleScheduleRepo(schedule), new NoContractRepo(), NullLogger<RateResolutionService>.Instance);

        var resultSet = await engine.ResolveBatchAsync(
        [
            Request("99213", lineNumber: 1),
            Request("29881", lineNumber: 2),
            Request("27447", lineNumber: 3),
        ]);

        // FS-MEDICAID is a Medicaid schedule at 70% of the stored line rates
        Assert.Equal(59.50m, resultSet.LineResults.Single(r => r.ProcedureCode == "99213").AllowedAmount);   // 85 × 0.70, unreduced
        Assert.Equal(3570m, resultSet.LineResults.Single(r => r.ProcedureCode == "27447").AllowedAmount);    // 5100 × 0.70, 100%
        Assert.Equal(798m, resultSet.LineResults.Single(r => r.ProcedureCode == "29881").AllowedAmount);     // 2280 × 0.70 × 50%
        Assert.All(resultSet.LineResults, r => Assert.Empty(r.Warnings));
    }

    private static PricingRequest Request(string code, int lineNumber) => new()
    {
        TenantId = Tenant,
        ProcedureCode = code,
        ProviderNpi = "1234567890",
        PlaceOfServiceCode = "11",
        ServiceDate = new DateTime(2024, 6, 1),
        PlanId = "plan",
        BilledAmount = 10_000m,
        Units = 1,
        LineNumber = lineNumber,
        TotalLineCount = 3,
    };

    private sealed class CapturingSeeder : CosmosDbSeeder
    {
        public CapturingSeeder() : base("unused-connection-string") { }

        public List<JObject> FeeScheduleDocuments { get; } = new();

        protected override Task WriteDocumentsAsync(
            string containerName, List<object> documents, CancellationToken cancellationToken)
        {
            if (containerName == ContainerNames.FeeSchedules)
                FeeScheduleDocuments.AddRange(documents.Select(d => JObject.Parse(JsonConvert.SerializeObject(d))));
            return Task.CompletedTask;
        }
    }

    private sealed class SingleScheduleRepo(FeeSchedule schedule) : IFeeScheduleRepository
    {
        public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
            => Task.FromResult<FeeSchedule?>(schedule);

        public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<FeeSchedule?>(schedule);

        public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
            => Task.FromResult<FeeScheduleLine?>(null);

        public Task<FeeSchedule> UpsertAsync(FeeSchedule s, CancellationToken ct = default)
            => Task.FromResult(s);

        public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeSchedule>>([schedule]);
    }

    private sealed class NoContractRepo : IProviderContractRepository
    {
        public Task<ProviderContract?> GetContractAsync(string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<ProviderContract?>(null);

        public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
            => Task.FromResult(contract);

        public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(string tenantId, string providerNpi, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderContract>>([]);
    }
}
