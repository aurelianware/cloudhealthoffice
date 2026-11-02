using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// State supplemental payments (<see cref="FeeScheduleType.Supplemental"/>),
/// with Medi-Cal Prop 56 dental as the case: APL 26-008 ends them for dates of
/// service on or after 2027-07-01, with a one-year claims runout.
/// </summary>
public class SupplementalPaymentTests
{
    private const string Tenant = "t1";
    private const string PlanVersionId = "6f8c1d2e-0000-4000-8000-000000000001";
    private const string PlanCode = "MEDI-CAL-DENTAL";
    private const string Prop56Notice =
        "Prop 56 supplemental payments are discontinued for dates of service on or after July 1, 2027.";

    private static FeeSchedule Prop56(DateTime? termDate = null, int? runoutMonths = 12, string? notice = Prop56Notice) => new()
    {
        Id = "t1:medi-cal-dental-prop-56:20170701",
        TenantId = Tenant,
        Name = "Medi-Cal Dental Prop 56",
        Type = FeeScheduleType.Supplemental,
        Jurisdiction = "CA",
        EffectiveDate = new DateTime(2017, 7, 1),
        TermDate = termDate,
        SupplementalForPlanIds = [PlanCode],
        ClaimsRunoutMonths = runoutMonths,
        DiscontinuedNotice = notice,
        Lines =
        [
            new FeeScheduleLine { ProcedureCode = "D1120", RateType = FeeScheduleRateType.FlatRate, Rate = 10m },
            new FeeScheduleLine { ProcedureCode = "D2391", RateType = FeeScheduleRateType.FlatRate, Rate = 25.50m },
        ],
    };

    private static FeeSchedule Sma() => new()
    {
        Id = "t1:sma",
        TenantId = Tenant,
        Name = "Medi-Cal Dental SMA",
        Type = FeeScheduleType.Medicaid,
        EffectiveDate = new DateTime(2017, 1, 1),
        Lines =
        [
            new FeeScheduleLine { ProcedureCode = "D1120", RateType = FeeScheduleRateType.FlatRate, Rate = 30m },
            new FeeScheduleLine { ProcedureCode = "D2391", RateType = FeeScheduleRateType.FlatRate, Rate = 80m },
            new FeeScheduleLine { ProcedureCode = "D0150", RateType = FeeScheduleRateType.FlatRate, Rate = 40m },
        ],
    };

    private static PricingRequest Request(
        string code, DateTime dos, DateTime? received, int line = 1, int total = 1, decimal units = 1m, decimal billed = 100m) => new()
    {
        TenantId = Tenant,
        ProcedureCode = code,
        ProviderNpi = "1234567893",
        PlaceOfServiceCode = "11",
        ServiceDate = dos,
        PlanId = PlanVersionId,
        PlanCode = PlanCode,
        BilledAmount = billed,
        Units = units,
        LineNumber = line,
        TotalLineCount = total,
        ClaimReceivedDate = received,
    };

    private static RateResolutionService Engine(params FeeSchedule[] supplemental)
    {
        var repo = new SupplementalRepo(Sma(), supplemental);
        return new RateResolutionService(repo, new InMemoryProviderContractRepo(null), NullLogger<RateResolutionService>.Instance);
    }

    [Fact]
    public async Task BeforeTheEndDate_PaysTheSupplementalApartFromAllowed()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1)));

        var result = await engine.ResolveAsync(Request("D1120", new DateTime(2027, 6, 30), new DateTime(2027, 7, 15)));

        Assert.Equal(30m, result.AllowedAmount);
        Assert.Equal(10m, result.SupplementalAmount);
        var payment = Assert.Single(result.SupplementalPayments);
        Assert.Equal("Medi-Cal Dental Prop 56", payment.FeeScheduleName);
        Assert.Empty(result.PaymentNotices);
    }

    [Fact]
    public async Task OnOrAfterTheEndDate_PaysNothingAndCarriesTheAplNotice()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1)));

        var result = await engine.ResolveAsync(Request("D1120", new DateTime(2027, 7, 1), new DateTime(2027, 7, 2)));

        Assert.Equal(30m, result.AllowedAmount);
        Assert.Equal(0m, result.SupplementalAmount);
        Assert.Empty(result.SupplementalPayments);
        Assert.Equal(Prop56Notice, Assert.Single(result.PaymentNotices));
    }

    [Fact]
    public async Task AnUnlistedProcedure_GetsNeitherPaymentNorNotice()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1)));

        var result = await engine.ResolveAsync(Request("D0150", new DateTime(2027, 8, 1), new DateTime(2027, 8, 2)));

        Assert.Equal(0m, result.SupplementalAmount);
        Assert.Empty(result.PaymentNotices);
    }

    [Fact]
    public async Task ReceivedOnTheLastDayOfTheRunout_IsPaid_TheDayAfterIsNot()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1)));

        var lastDay = await engine.ResolveAsync(Request("D2391", new DateTime(2027, 6, 30), new DateTime(2028, 6, 30)));
        var late = await engine.ResolveAsync(Request("D2391", new DateTime(2027, 6, 30), new DateTime(2028, 7, 1)));

        Assert.Equal(25.50m, lastDay.SupplementalAmount);
        Assert.Equal(0m, late.SupplementalAmount);
        Assert.Contains("more than 12 months after", Assert.Single(late.PaymentNotices));
        Assert.Equal(80m, late.AllowedAmount);
    }

    [Fact]
    public async Task UnknownReceivedDate_PaysButWarnsTheRunoutWasNotChecked()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1)));

        var result = await engine.ResolveAsync(Request("D1120", new DateTime(2027, 1, 5), received: null));

        Assert.Equal(10m, result.SupplementalAmount);
        Assert.Contains(result.Warnings, w => w.Contains("runout was not checked"));
    }

    [Fact]
    public async Task NoNotice_OnTheSchedule_FallsBackToAGenericOne()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1), notice: null));

        var result = await engine.ResolveAsync(Request("D1120", new DateTime(2027, 9, 1), new DateTime(2027, 9, 2)));

        Assert.Equal(
            "Medi-Cal Dental Prop 56 supplemental payments are discontinued for dates of service on or after July 1, 2027.",
            Assert.Single(result.PaymentNotices));
    }

    [Fact]
    public async Task Batch_AddsTheSupplementalAfterMultipleProcedureRanking_PerUnit()
    {
        var engine = Engine(Prop56(termDate: new DateTime(2027, 7, 1)));
        var dos = new DateTime(2027, 3, 1);

        var set = await engine.ResolveBatchAsync(
        [
            Request("D2391", dos, dos.AddDays(3), line: 1, total: 2, units: 2m, billed: 300m),
            Request("D1120", dos, dos.AddDays(3), line: 2, total: 2),
        ]);

        Assert.Equal(51m, set.LineResults[0].SupplementalAmount);
        Assert.Equal(10m, set.LineResults[1].SupplementalAmount);
        Assert.Equal(160m, set.LineResults[0].AllowedAmount);
    }

    [Fact]
    public async Task ASchedule_ForAnotherPlan_PaysNothing()
    {
        var other = Prop56();
        other.SupplementalForPlanIds = ["SOME-OTHER-PLAN"];
        var engine = Engine(other);

        var result = await engine.ResolveAsync(Request("D1120", new DateTime(2027, 1, 5), new DateTime(2027, 1, 6)));

        Assert.Equal(0m, result.SupplementalAmount);
    }

    [Fact]
    public void ModifierSpecificLine_WinsOverTheCodeLine()
    {
        var schedule = Prop56();
        schedule.Lines.Add(new FeeScheduleLine { ProcedureCode = "D1120", Modifier = "UA", Rate = 99m });

        var withModifier = Request("D1120", new DateTime(2027, 1, 5), null) with { Modifiers = ["UA"] };
        var without = Request("D1120", new DateTime(2027, 1, 5), null);

        Assert.Equal(99m, SupplementalPaymentCalculator.FindLine(schedule, withModifier)!.Rate);
        Assert.Equal(10m, SupplementalPaymentCalculator.FindLine(schedule, without)!.Rate);
    }

    [Fact]
    public async Task CachingRepository_PassesSupplementalSchedulesThrough_AndForgetsThemOnUpsert()
    {
        var inner = new SupplementalRepo(Sma(), Prop56());
        var cache = new CachingFeeScheduleRepository(inner, new InMemoryProviderContractRepo(null), new MemoryCache(new MemoryCacheOptions()));
        var keys = new[] { PlanVersionId, PlanCode };

        var first = await cache.GetSupplementalSchedulesAsync(Tenant, keys, new DateTime(2027, 1, 5));
        Assert.Single(first);
        Assert.Null(first[0].TermDate);

        var ended = Prop56(termDate: new DateTime(2027, 7, 1));
        await cache.UpsertAsync(ended);

        var second = await cache.GetSupplementalSchedulesAsync(Tenant, keys, new DateTime(2027, 1, 5));
        Assert.Equal(new DateTime(2027, 7, 1), Assert.Single(second).TermDate);
    }

    /// <summary>A plan default schedule plus supplemental schedules, matched as the MongoDB store does.</summary>
    private sealed class SupplementalRepo(FeeSchedule planDefault, params FeeSchedule[] supplemental) : IFeeScheduleRepository
    {
        private readonly Dictionary<string, FeeSchedule> _supplemental = supplemental.ToDictionary(s => s.Id);

        public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
            => Task.FromResult(id == planDefault.Id ? planDefault : _supplemental.GetValueOrDefault(id));

        public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<FeeSchedule?>(planDefault);

        public Task<IReadOnlyList<FeeSchedule>> GetSupplementalSchedulesAsync(
            string tenantId, IReadOnlyCollection<string> planKeys, DateTime serviceDate, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeSchedule>>(_supplemental.Values
                .Where(s => s.TenantId == tenantId
                            && s.Type == FeeScheduleType.Supplemental
                            && (s.SupplementalForPlanIds ?? []).Intersect(planKeys).Any()
                            && s.EffectiveDate <= serviceDate)
                .ToList());

        public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
            => Task.FromResult<FeeScheduleLine?>(null);

        public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
        {
            _supplemental[schedule.Id] = schedule;
            return Task.FromResult(schedule);
        }

        public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeSchedule>>(_supplemental.Values.ToList());
    }
}
