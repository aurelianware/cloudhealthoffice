using CloudHealthOffice.FeeScheduleEngine.Configuration;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// The dual-read window of ADR 016 phase 4: every claim priced with both the
/// legacy and the canonical provider contract, <b>paid on legacy</b>, with the
/// difference reported per line.
///
/// <para>
/// The load-bearing assertion in every test here is the same one: the allowed
/// amount the caller gets back is the legacy contract's. The window is meant to
/// be invisible to the money, and the only way to be sure of that is to make a
/// canonical contract that pays something else and check the claim is still paid
/// the old way. The rest covers what the window is for — that a difference is
/// classified and recorded — and the ways the shadow half can fail without
/// taking a claim down with it.
/// </para>
/// </summary>
public class ContractDualReadTests
{
    private const string Tenant = "test-tenant";
    private const string OtherTenant = "other-tenant";
    private const string PlanId = "plan-001";
    private const string ProviderNpi = "1234567890";
    private const string LegacyScheduleId = "comm-legacy";
    private const string CanonicalScheduleId = "comm-canonical";
    private const string PlanDefaultScheduleId = "plan-default";

    // ═══════════════════════════════════════════════════════════════════
    // THE CLAIM IS PAID ON LEGACY
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ACanonicalContractThatPaysMore_DoesNotChangeWhatTheClaimIsPaid()
    {
        var (engine, recorder) = Window(
            legacy: LegacyContract(LegacyScheduleId),
            canonical: CanonicalContract(CanonicalScheduleId));

        var result = await engine.ResolveAsync(Request("99213"));

        // The legacy schedule pays 110, the canonical one 130.
        Assert.Equal(110m, result.AllowedAmount);
        Assert.Equal(LegacyScheduleId, result.FeeScheduleId);

        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.AmountDelta, parity.Outcome);
        Assert.Equal(110m, parity.PaidAmount);
        Assert.Equal(130m, parity.CanonicalAmount);
        Assert.Equal(20m, parity.Delta);
        Assert.Contains(CanonicalScheduleId, parity.Reason);
    }

    [Fact]
    public async Task AcrossABatch_EveryLineIsPaidOnLegacy_AndEveryLineIsCompared()
    {
        var (engine, recorder) = Window(
            legacy: LegacyContract(LegacyScheduleId),
            canonical: CanonicalContract(CanonicalScheduleId));

        var result = await engine.ResolveBatchAsync(
        [
            Request("99213", lineNumber: 1, totalLines: 2),
            Request("27447", lineNumber: 2, totalLines: 2),
        ]);

        Assert.Equal([110m, 1500m], result.LineResults.Select(r => r.AllowedAmount));
        Assert.Equal(2, recorder.Recorded.Count);
        Assert.All(recorder.Recorded, p => Assert.Equal(ParityOutcome.AmountDelta, p.Outcome));
        Assert.Equal([1, 2], recorder.Recorded.Select(p => p.LineNumber));
    }

    // ═══════════════════════════════════════════════════════════════════
    // WHAT COUNTS AS A DIFFERENCE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TheSameContractInBothStores_IsAMatch()
    {
        var (engine, recorder) = Window(
            legacy: LegacyContract(LegacyScheduleId),
            canonical: CanonicalContract(LegacyScheduleId));

        await engine.ResolveAsync(Request("99213"));

        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.Match, parity.Outcome);
        Assert.True(parity.IsMatch);
        Assert.Equal(0m, parity.Delta);
        Assert.Null(parity.Reason);
    }

    [Fact]
    public async Task TheSameAmountFromADifferentSchedule_IsRecordedAsADifferentRoute()
    {
        // Both schedules price 29881 at 800, by two different documents. The
        // money is the same today; the contracts do not say the same thing, and
        // the next rate load is where that matters.
        var (engine, recorder) = Window(
            legacy: LegacyContract(LegacyScheduleId),
            canonical: CanonicalContract(CanonicalScheduleId));

        var result = await engine.ResolveAsync(Request("29881"));

        Assert.Equal(800m, result.AllowedAmount);
        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.SameAmountDifferentRoute, parity.Outcome);
        Assert.Equal(0m, parity.Delta);
        Assert.Contains("schedule", parity.Reason);
    }

    [Fact]
    public async Task ACanonicalTermTheEngineCannotPrice_IsRecordedAsTheCanonicalContractPending()
    {
        // A stop-loss provision is stored and not evaluated, so the engine pends
        // rather than paying a rate the contract does not state. On the shadow
        // side that is the window doing its job: it is found before cutover.
        var canonical = CanonicalContract(CanonicalScheduleId);
        canonical.ReimbursementTerms[0].StopLoss = new StopLossTerm
        {
            ThresholdBilled = 50_000m,
            PercentAboveThreshold = 0.80m,
        };

        var (engine, recorder) = Window(legacy: LegacyContract(LegacyScheduleId), canonical: canonical);

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(110m, result.AllowedAmount);
        Assert.NotEqual(RateSource.Unresolved, result.RateSource);

        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.CanonicalPends, parity.Outcome);
        Assert.Contains("stop-loss", parity.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProviderTheBackfillHasNotReached_IsRecordedAsHavingNoCanonicalContract()
    {
        var (engine, recorder) = Window(legacy: LegacyContract(LegacyScheduleId), canonical: null);

        var result = await engine.ResolveAsync(Request("99213"));

        // Paid on legacy, as always. The shadow pass has no contract to price
        // with, so it falls to the plan default — which is a finding about the
        // backfill, not about the contract.
        Assert.Equal(110m, result.AllowedAmount);

        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.NoCanonicalContract, parity.Outcome);
        Assert.Equal(95m, parity.CanonicalAmount);
        Assert.Contains("no canonical contract", parity.Reason);
    }

    [Fact]
    public async Task AProviderWithNeitherContract_IsAMatchAtThePlanDefault()
    {
        var (engine, recorder) = Window(legacy: null, canonical: null);

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(95m, result.AllowedAmount);
        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.Match, parity.Outcome);
    }

    // ═══════════════════════════════════════════════════════════════════
    // THE WINDOW CANNOT BREAK A CLAIM
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenTheCanonicalStoreCannotBeRead_TheClaimIsStillPaidOnLegacy()
    {
        var ledger = new ContractShadowLedger();
        var contracts = new DualReadProviderContractRepository(
            legacy: new CountingContractRepo(LegacyContract(LegacyScheduleId)),
            canonical: new ThrowingContractRepo(),
            ledger: ledger,
            options: InWindow(),
            logger: NullLogger<DualReadProviderContractRepository>.Instance);

        var recorder = new RecordingParityRecorder();
        var engine = Decorate(contracts, ledger, recorder);

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(110m, result.AllowedAmount);

        // An unreadable canonical store looks exactly like a provider the
        // backfill has not reached: nothing is known about parity, and the claim
        // is unaffected either way.
        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.NoCanonicalContract, parity.Outcome);
    }

    [Fact]
    public async Task WhenTheShadowPassItselfThrows_TheClaimIsStillPaid_AndTheLineIsRecordedAsFailed()
    {
        var ledger = new ContractShadowLedger();
        var contracts = DualRead(LegacyContract(LegacyScheduleId), CanonicalContract(CanonicalScheduleId), ledger);
        var recorder = new RecordingParityRecorder();

        var engine = new ShadowPricingRateResolutionService(
            paid: NewEngine(contracts),
            shadow: new ThrowingRateResolutionService(),
            ledger: ledger,
            recorder: recorder,
            logger: NullLogger<ShadowPricingRateResolutionService>.Instance);

        var result = await engine.ResolveAsync(Request("99213"));

        Assert.Equal(110m, result.AllowedAmount);
        var parity = Assert.Single(recorder.Recorded);
        Assert.Equal(ParityOutcome.ShadowFailed, parity.Outcome);
        Assert.Equal(110m, parity.PaidAmount);
    }

    [Fact]
    public async Task OneClaimIsNeverShadowedAgainstAnothersContracts()
    {
        var ledger = new ContractShadowLedger();
        var contracts = DualRead(LegacyContract(LegacyScheduleId), CanonicalContract(CanonicalScheduleId), ledger);
        var recorder = new RecordingParityRecorder();
        var engine = Decorate(contracts, ledger, recorder);

        await engine.ResolveAsync(Request("99213"));
        await engine.ResolveAsync(Request("27447"));

        // Two claims, two comparisons — not three, which is what a ledger that
        // kept the first claim's lookups would produce.
        Assert.Equal(2, recorder.Recorded.Count);
        Assert.Equal(["99213", "27447"], recorder.Recorded.Select(p => p.ProcedureCode));
    }

    // ═══════════════════════════════════════════════════════════════════
    // WHO IS IN THE WINDOW
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ATenantOutsideTheWindow_IsPricedOnceAndNotCompared()
    {
        var ledger = new ContractShadowLedger();
        var canonical = new CountingContractRepo(CanonicalContract(CanonicalScheduleId));
        var contracts = new DualReadProviderContractRepository(
            legacy: new CountingContractRepo(LegacyContract(LegacyScheduleId)),
            canonical: canonical,
            ledger: ledger,
            options: InWindow(OtherTenant),
            logger: NullLogger<DualReadProviderContractRepository>.Instance);

        var recorder = new RecordingParityRecorder();
        var result = await Decorate(contracts, ledger, recorder).ResolveAsync(Request("99213"));

        Assert.Equal(110m, result.AllowedAmount);
        Assert.Empty(recorder.Recorded);
        Assert.Equal(0, canonical.Reads);
    }

    [Fact]
    public async Task TheCanonicalContractIsReadOncePerClaim_NotOncePerLine()
    {
        var ledger = new ContractShadowLedger();
        var canonical = new CountingContractRepo(CanonicalContract(CanonicalScheduleId));
        var contracts = new DualReadProviderContractRepository(
            legacy: new CountingContractRepo(LegacyContract(LegacyScheduleId)),
            canonical: canonical,
            ledger: ledger,
            options: InWindow(),
            logger: NullLogger<DualReadProviderContractRepository>.Instance);

        await Decorate(contracts, ledger, new RecordingParityRecorder()).ResolveBatchAsync(
        [
            Request("99213", lineNumber: 1, totalLines: 3),
            Request("27447", lineNumber: 2, totalLines: 3),
            Request("29881", lineNumber: 3, totalLines: 3),
        ]);

        // Three lines, one provider and plan, one extra query. The shadow pass
        // adds none of its own: it prices out of the ledger.
        Assert.Equal(1, canonical.Reads);
    }

    [Theory]
    [InlineData(true, "", Tenant, true)]
    [InlineData(true, Tenant, Tenant, true)]
    [InlineData(true, Tenant, OtherTenant, false)]
    [InlineData(false, "", Tenant, false)]
    public void TheWindowIsPerTenant_AndOffUntilADeploymentOpensIt(
        bool enabled, string tenants, string tenantId, bool expected)
    {
        var options = new ContractDualReadOptions
        {
            Enabled = enabled,
            Tenants = tenants.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
        };

        Assert.Equal(expected, options.IsInWindow(tenantId));
    }

    [Fact]
    public async Task TheShadowRepository_WritesNothing()
    {
        var repository = new ShadowContractRepository(new ContractShadowLedger());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => repository.UpsertAsync(LegacyContract(LegacyScheduleId)));
    }

    [Fact]
    public async Task AWriteInsideTheWindow_GoesToTheCanonicalStoreOnly()
    {
        var ledger = new ContractShadowLedger();
        var legacy = new CountingContractRepo(null);
        var canonical = new CountingContractRepo(null);
        var contracts = new DualReadProviderContractRepository(
            legacy, canonical, ledger, InWindow(),
            NullLogger<DualReadProviderContractRepository>.Instance);

        await contracts.UpsertAsync(CanonicalContract(CanonicalScheduleId));

        Assert.Equal(1, canonical.Writes);
        Assert.Equal(0, legacy.Writes);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static ContractDualReadOptions InWindow(params string[] tenants) => new()
    {
        Enabled = true,
        Tenants = tenants.ToHashSet(StringComparer.Ordinal),
    };

    private static (IRateResolutionService Engine, RecordingParityRecorder Recorder) Window(
        ProviderContract? legacy, ProviderContract? canonical)
    {
        var ledger = new ContractShadowLedger();
        var recorder = new RecordingParityRecorder();
        return (Decorate(DualRead(legacy, canonical, ledger), ledger, recorder), recorder);
    }

    private static DualReadProviderContractRepository DualRead(
        ProviderContract? legacy, ProviderContract? canonical, ContractShadowLedger ledger)
        => new(
            legacy: new CountingContractRepo(legacy),
            canonical: new CountingContractRepo(canonical),
            ledger: ledger,
            options: InWindow(),
            logger: NullLogger<DualReadProviderContractRepository>.Instance);

    private static ShadowPricingRateResolutionService Decorate(
        IProviderContractRepository contracts,
        ContractShadowLedger ledger,
        IPricingParityRecorder recorder)
        => new(
            paid: NewEngine(contracts),
            shadow: NewEngine(new ShadowContractRepository(ledger)),
            ledger: ledger,
            recorder: recorder,
            logger: NullLogger<ShadowPricingRateResolutionService>.Instance);

    private static RateResolutionService NewEngine(IProviderContractRepository contracts)
    {
        var schedules = new InMemoryFeeScheduleRepo(PlanDefaultSchedule());
        schedules.AddSchedule(LegacySchedule());
        schedules.AddSchedule(CanonicalSchedule());
        return new RateResolutionService(schedules, contracts, NullLogger<RateResolutionService>.Instance);
    }

    private static ProviderContract LegacyContract(string feeScheduleId)
    {
        var contract = BareContract();
        contract.FeeScheduleId = feeScheduleId;
        return contract;
    }

    private static ProviderContract CanonicalContract(string feeScheduleId)
    {
        var contract = BareContract();
        contract.LegalContractId = "legal-contract-1";
        contract.Origin = "legal";
        contract.ReimbursementTerms =
        [
            new ReimbursementTerm
            {
                Method = ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = feeScheduleId,
                Description = "Commercial rate sheet",
            },
        ];
        return contract;
    }

    private static ProviderContract BareContract() => new()
    {
        Id = ProviderContract.MakeId(Tenant, ProviderNpi, PlanId),
        TenantId = Tenant,
        ProviderNpi = ProviderNpi,
        PlanId = PlanId,
        NetworkStatus = NetworkStatus.InNetwork,
        EffectiveDate = new DateTime(2026, 1, 1),
    };

    // 29881 is deliberately the same rate in both schedules: the same money by
    // two different routes is its own parity outcome.
    private static FeeSchedule LegacySchedule() => Commercial(
        LegacyScheduleId, "Commercial (legacy contract)",
        ("99213", 110m), ("27447", 1500m), ("29881", 800m));

    private static FeeSchedule CanonicalSchedule() => Commercial(
        CanonicalScheduleId, "Commercial (canonical contract)",
        ("99213", 130m), ("27447", 1650m), ("29881", 800m));

    private static FeeSchedule PlanDefaultSchedule()
    {
        var schedule = Commercial(PlanDefaultScheduleId, "Plan Default", ("99213", 95m), ("27447", 1200m));
        schedule.DefaultForPlanId = PlanId;
        return schedule;
    }

    private static FeeSchedule Commercial(
        string id, string name, params (string Code, decimal Rate)[] lines) => new()
    {
        Id = id, TenantId = Tenant, Name = name,
        Type = FeeScheduleType.Commercial,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = lines
            .Select(l => new FeeScheduleLine
            {
                ProcedureCode = l.Code,
                Rate = l.Rate,
                MultipleProcedureIndicator = l.Code == "99213"
                    ? MultipleProcedureIndicator.NoReduction
                    : MultipleProcedureIndicator.StandardSurgery,
            })
            .ToList(),
    };

    private static PricingRequest Request(
        string procedureCode, int lineNumber = 1, int totalLines = 1, decimal billed = 5_000m) => new()
    {
        TenantId = Tenant,
        ProcedureCode = procedureCode,
        ProviderNpi = ProviderNpi,
        PlaceOfServiceCode = "11",
        ServiceDate = new DateTime(2026, 3, 8),
        PlanId = PlanId,
        BilledAmount = billed,
        LineNumber = lineNumber,
        TotalLineCount = totalLines,
    };
}

/// <summary>Keeps every parity record, so a test can read what the window saw.</summary>
internal sealed class RecordingParityRecorder : IPricingParityRecorder
{
    private readonly List<LineParity> _recorded = [];

    public IReadOnlyList<LineParity> Recorded => _recorded;

    public void Record(LineParity parity) => _recorded.Add(parity);
}

/// <summary>A canonical store that is down.</summary>
internal sealed class ThrowingContractRepo : IProviderContractRepository
{
    public Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
        => throw new TimeoutException("the canonical contract collection is unreachable");

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
        => throw new TimeoutException("the canonical contract collection is unreachable");

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
        => throw new TimeoutException("the canonical contract collection is unreachable");
}

/// <summary>A shadow pass that breaks, to prove it cannot take the claim with it.</summary>
internal sealed class ThrowingRateResolutionService : IRateResolutionService
{
    public Task<PricingResult> ResolveAsync(PricingRequest request, CancellationToken ct = default)
        => throw new InvalidOperationException("the shadow pass failed");

    public Task<PricingResultSet> ResolveBatchAsync(
        IReadOnlyList<PricingRequest> requests, CancellationToken ct = default)
        => throw new InvalidOperationException("the shadow pass failed");
}

/// <summary>Counts what the dual read actually asked each store for.</summary>
internal sealed class CountingContractRepo : IProviderContractRepository
{
    private readonly ProviderContract? _contract;

    public CountingContractRepo(ProviderContract? contract) => _contract = contract;

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(_contract);
    }

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
    {
        Writes++;
        return Task.FromResult(contract);
    }

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProviderContract>>(
            _contract is null ? [] : [_contract]);
}
