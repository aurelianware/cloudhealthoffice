using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

/// <summary>
/// The plan-default fee schedule the MCC validator seeds must price every line the
/// institutional generator can emit, and the generator's expected allowed amounts must
/// be what the FeeScheduleEngine computes from it.
/// </summary>
public class PlanDefaultFeeScheduleTests
{
    private static readonly string[] SubTypes =
        ["inpatient", "outpatient", "emergency", "observation", "stoploss", "skillednursing"];

    private static IEnumerable<string> AllGeneratorRevenueCodes() =>
        RevenueCodeSets.Inpatient
            .Concat(RevenueCodeSets.Outpatient)
            .Concat(RevenueCodeSets.Emergency)
            .Concat(RevenueCodeSets.Observation)
            .Concat(RevenueCodeSets.SkilledNursing)
            .Select(c => c.Code)
            .Distinct();

    [Fact]
    public void PlanDefault_HasBlankProcedureFlatRateLineForEveryGeneratorRevenueCode()
    {
        var schedule = SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule();
        var revenueLines = schedule.Lines.Where(l => l.RevenueCode is not null).ToList();

        foreach (var code in AllGeneratorRevenueCodes())
        {
            var line = Assert.Single(revenueLines, l => l.RevenueCode == code);
            Assert.Equal(string.Empty, line.ProcedureCode);
            Assert.Equal("FlatRate", line.RateType);
            Assert.True(line.AllowedAmount > 0m, $"revenue code {code} has no rate");
        }

        Assert.Equal("Custom", schedule.Type);
        Assert.Null(schedule.PercentOfMedicare);
    }

    [Fact]
    public void PlanDefault_KeepsMedicaidProcedureCoverage_AndIsDeterministic()
    {
        var medicaid = SyntheticFeeScheduleGenerator.Generate(42).Single(fs => fs.FeeScheduleId == "FS-MEDICAID");
        var first = SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule();
        var second = SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule();

        var procedureLines = first.Lines.Where(l => l.RevenueCode is null).ToList();
        Assert.Equal(
            medicaid.Lines.Select(l => (l.ProcedureCode, l.AllowedAmount, l.MultipleProcedureIndicator)),
            procedureLines.Take(medicaid.Lines.Count).Select(l => (l.ProcedureCode, l.AllowedAmount, l.MultipleProcedureIndicator)));
        Assert.Equal(new[] { "69436", "99283" }, procedureLines.Skip(medicaid.Lines.Count).Select(l => l.ProcedureCode));
        Assert.All(procedureLines, l => Assert.NotNull(l.MultipleProcedureIndicator));
        Assert.All(first.Lines, l => Assert.Equal("FlatRate", l.RateType));

        Assert.Equal(
            first.Lines.Select(l => (l.ProcedureCode, l.RevenueCode, l.AllowedAmount)),
            second.Lines.Select(l => (l.ProcedureCode, l.RevenueCode, l.AllowedAmount)));
    }

    [Fact]
    public void PlanDefault_HasARateForEveryProfessionalAndDentalReferenceCode()
    {
        var codes = new[]
            {
                ProcedureCodes.OfficeVisits, ProcedureCodes.Surgical, ProcedureCodes.Telemedicine,
                ProcedureCodes.LabPathology, ProcedureCodes.Bilateral, ProcedureCodes.AssistantSurgeon,
                ProcedureCodes.PostOpVisits, ProcedureCodes.BehavioralHealth,
                DentalCodes.Preventive, DentalCodes.Restorative, DentalCodes.Endodontics,
                DentalCodes.Periodontics, DentalCodes.Orthodontics, DentalCodes.OralSurgery,
            }
            .SelectMany(set => set.Select(c => c.Code))
            .Distinct();
        var scheduled = SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule().Lines
            .Where(l => l.RevenueCode is null)
            .Select(l => l.ProcedureCode)
            .ToHashSet();

        Assert.DoesNotContain(codes, c => !scheduled.Contains(c));
    }

    [Fact]
    public void ExpectedAllowed_FromSeededRates_NeverExceedsBilled()
    {
        var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider());
        var random = new Random(7);

        for (var i = 0; i < 600; i++)
        {
            var claim = generator.Generate(i, SubTypes[i % SubTypes.Length], random);
            var expected = claim.ExpectedOutcome!;

            foreach (var line in claim.Lines)
            {
                var rate = SyntheticFeeScheduleGenerator.RevenueCodeRate(line.RevenueCode);
                Assert.True(rate is not null, $"no plan-default rate for revenue code {line.RevenueCode}");
                var lineOutcome = expected.LineOutcomes.Single(o => o.LineNumber == line.LineNumber);
                Assert.Equal(rate!.Value * line.Units, lineOutcome.AllowedAmount);
                Assert.True(
                    lineOutcome.AllowedAmount <= line.ChargeAmount * line.Units,
                    $"{claim.ClaimId} line {line.LineNumber} ({line.RevenueCode}) allowed {lineOutcome.AllowedAmount} > billed {line.ChargeAmount * line.Units}");
            }

            Assert.Equal(expected.LineOutcomes.Sum(o => o.AllowedAmount), expected.ExpectedAllowedAmount);
            Assert.True(expected.ExpectedAllowedAmount <= claim.TotalCharges);
        }
    }

    [Fact]
    public async Task Engine_PricesGeneratedInstitutionalClaims_AtExpectedAllowed()
    {
        const string tenant = "mcc-benchmark";
        const string planId = "0b7f5c1e-3a7d-4f61-9a0e-2a1c5b6d7e8f";
        var engine = new RateResolutionService(
            new PlanDefaultOnlyRepo(ToEngineSchedule(SyntheticFeeScheduleGenerator.GeneratePlanDefaultSchedule(), tenant, planId)),
            new NoContractRepo(),
            NullLogger<RateResolutionService>.Instance);
        var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider());
        var random = new Random(11);

        for (var i = 0; i < 60; i++)
        {
            var claim = generator.Generate(i, SubTypes[i % SubTypes.Length], random);
            var requests = claim.Lines.Select(l => new PricingRequest
            {
                TenantId = tenant,
                PlanId = planId,
                ProviderNpi = claim.BillingProvider.Npi,
                ProcedureCode = l.ProcedureCode, // generator echoes the revenue code here
                RevenueCode = l.RevenueCode,
                Units = l.Units,
                BilledAmount = l.ChargeAmount * l.Units,
                ServiceDate = l.ServiceDate,
                PlaceOfServiceCode = claim.PlaceOfService,
                BillType = claim.BillType,
                IsInstitutional = true,
                LineNumber = l.LineNumber,
                TotalLineCount = claim.Lines.Count,
            }).ToList();

            var priced = await engine.ResolveBatchAsync(requests);

            foreach (var result in priced.LineResults)
            {
                Assert.Equal(RateSource.PlanDefault, result.RateSource);
                Assert.Empty(result.Warnings);
                var expected = claim.ExpectedOutcome!.LineOutcomes.Single(o => o.LineNumber == result.LineNumber);
                Assert.Equal(expected.AllowedAmount, result.AllowedAmount);
            }
        }
    }

    private static FeeSchedule ToEngineSchedule(SyntheticFeeSchedule source, string tenant, string planId) => new()
    {
        Id = $"{tenant}:plan-default:{planId}",
        TenantId = tenant,
        Name = source.Name,
        Type = FeeScheduleType.Custom,
        DefaultForPlanId = planId,
        EffectiveDate = source.EffectiveDate,
        Lines = source.Lines.Select(l => new FeeScheduleLine
        {
            ProcedureCode = l.ProcedureCode,
            RevenueCode = l.RevenueCode,
            Modifier = l.Modifier,
            RateType = FeeScheduleRateType.FlatRate,
            Rate = l.AllowedAmount,
            MultipleProcedureIndicator = (MultipleProcedureIndicator?)l.MultipleProcedureIndicator,
        }).ToList(),
    };

    private sealed class PlanDefaultOnlyRepo(FeeSchedule schedule) : IFeeScheduleRepository
    {
        public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
            => Task.FromResult<FeeSchedule?>(id == schedule.Id ? schedule : null);

        public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<FeeSchedule?>(
                tenantId == schedule.TenantId && planId == schedule.DefaultForPlanId && serviceDate >= schedule.EffectiveDate
                    ? schedule
                    : null);

        public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
            => Task.FromResult<FeeScheduleLine?>(null);

        public Task<FeeSchedule> UpsertAsync(FeeSchedule s, CancellationToken ct = default) => Task.FromResult(s);

        public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeSchedule>>([schedule]);
    }

    private sealed class NoContractRepo : IProviderContractRepository
    {
        public Task<ProviderContract?> GetContractAsync(string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<ProviderContract?>(null);

        public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default) => Task.FromResult(contract);

        public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(string tenantId, string providerNpi, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderContract>>([]);
    }
}
