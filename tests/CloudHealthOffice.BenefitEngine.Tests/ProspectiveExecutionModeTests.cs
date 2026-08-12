using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Verifies that <see cref="AdjudicationExecutionMode.Prospective"/> makes the
/// benefit engine side-effect free: it computes the identical cost-sharing
/// waterfall as a production run but never persists accumulator updates.
/// The default (<see cref="AdjudicationExecutionMode.Production"/>) still
/// writes — existing adjudication behavior is unchanged.
/// </summary>
public class ProspectiveExecutionModeTests
{
    // ── Fixtures ────────────────────────────────────────────────────────

    private static BenefitPlanConfig OfficeVisitPlan(
        decimal individualDeductible = 500,
        InpatientPricingMethod inpatientMethod = InpatientPricingMethod.PerLine)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            PlanName = "Test Plan",
            PlanType = PlanType.PPO,
            PlanYear = "2026",
            IndividualDeductible = individualDeductible,
            FamilyDeductible = 1500,
            IndividualOopMax = 3000,
            FamilyOopMax = 9000,
            DefaultInpatientPricingMethod = inpatientMethod,
            Categories =
            [
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "98",
                    ServiceTypeDescription = "Office Visit",
                    IsCovered = true,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 30 },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ]
                },
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "48",
                    ServiceTypeDescription = "Hospital - Inpatient",
                    IsCovered = true,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ]
                },
            ]
        };

    private static BenefitResolutionRequest OfficeVisitRequest(
        Guid planId, AdjudicationExecutionMode mode)
        => new()
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = planId,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = NetworkTier.InNetwork,
            ClaimId = Guid.NewGuid().ToString(),
            ExecutionMode = mode,
            Lines =
            [
                new ClaimLineInput
                {
                    LineNumber = 1, ProcedureCode = "99213", PlaceOfService = "11",
                    BilledAmount = 150m, Units = 1
                }
            ],
            AllowedAmounts = new Dictionary<int, decimal> { [1] = 150m }
        };

    private static BenefitCalculationEngine Engine(
        BenefitPlanConfig plan, RecordingAccumulatorService acc, string categoryCode = "98")
        => new(
            new FixedCategoryResolver(categoryCode, plan.GetFirstCategory(categoryCode)?.ServiceTypeDescription ?? "x"),
            new InMemoryBenefitPlanProvider(plan),
            acc,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Prospective_DoesNotPersistAccumulators()
    {
        var plan = OfficeVisitPlan();
        var acc = new RecordingAccumulatorService();
        var engine = Engine(plan, acc);

        var result = await engine.CalculateAsync(
            OfficeVisitRequest(plan.Id, AdjudicationExecutionMode.Prospective));

        Assert.True(result.Success);
        Assert.Equal(0, acc.ApplyUpdatesCallCount);
        Assert.Empty(acc.CapturedUpdates);
        // Read-only runs skip the accumulatorWrite stage entirely.
        Assert.DoesNotContain("accumulatorWrite", result.Timings.Keys);
    }

    [Fact]
    public async Task Production_PersistsAccumulators()
    {
        var plan = OfficeVisitPlan();
        var acc = new RecordingAccumulatorService();
        var engine = Engine(plan, acc);

        var result = await engine.CalculateAsync(
            OfficeVisitRequest(plan.Id, AdjudicationExecutionMode.Production));

        Assert.True(result.Success);
        Assert.Equal(1, acc.ApplyUpdatesCallCount);
        Assert.NotEmpty(acc.CapturedUpdates);
        Assert.Contains("accumulatorWrite", result.Timings.Keys);
    }

    [Fact]
    public async Task DefaultExecutionMode_IsProduction()
    {
        var plan = OfficeVisitPlan();
        var acc = new RecordingAccumulatorService();
        var engine = Engine(plan, acc);

        // A request that never sets ExecutionMode must default to Production.
        var defaultModeRequest = new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = plan.Id,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = NetworkTier.InNetwork,
            ClaimId = Guid.NewGuid().ToString(),
            Lines =
            [
                new ClaimLineInput
                {
                    LineNumber = 1, ProcedureCode = "99213", PlaceOfService = "11",
                    BilledAmount = 150m, Units = 1
                }
            ],
            AllowedAmounts = new Dictionary<int, decimal> { [1] = 150m }
        };

        Assert.Equal(AdjudicationExecutionMode.Production, defaultModeRequest.ExecutionMode);

        await engine.CalculateAsync(defaultModeRequest);
        Assert.Equal(1, acc.ApplyUpdatesCallCount);
    }

    [Fact]
    public async Task Prospective_ComputesIdenticalCostSharingToProduction()
    {
        var plan = OfficeVisitPlan(individualDeductible: 0); // deductible met → copay+coinsurance

        var prodAcc = new RecordingAccumulatorService();
        var prospAcc = new RecordingAccumulatorService();

        var prod = await Engine(plan, prodAcc)
            .CalculateAsync(OfficeVisitRequest(plan.Id, AdjudicationExecutionMode.Production));
        var prosp = await Engine(plan, prospAcc)
            .CalculateAsync(OfficeVisitRequest(plan.Id, AdjudicationExecutionMode.Prospective));

        var prodLine = prod.Lines.Single();
        var prospLine = prosp.Lines.Single();

        Assert.Equal(prodLine.CopayAmount, prospLine.CopayAmount);
        Assert.Equal(prodLine.CoinsuranceAmount, prospLine.CoinsuranceAmount);
        Assert.Equal(prodLine.MemberResponsibility, prospLine.MemberResponsibility);
        Assert.Equal(prodLine.PlanPaidAmount, prospLine.PlanPaidAmount);
        Assert.Equal(prod.Totals.TotalPlanPaid, prosp.Totals.TotalPlanPaid);

        // Only the production run wrote to storage.
        Assert.Equal(1, prodAcc.ApplyUpdatesCallCount);
        Assert.Equal(0, prospAcc.ApplyUpdatesCallCount);
    }

    [Fact]
    public async Task Prospective_SnapshotReflectsProjectedBalancesWithoutPersisting()
    {
        var plan = OfficeVisitPlan(individualDeductible: 500);
        var acc = new RecordingAccumulatorService();
        var engine = Engine(plan, acc);

        var result = await engine.CalculateAsync(
            OfficeVisitRequest(plan.Id, AdjudicationExecutionMode.Prospective));

        // The in-memory snapshot shows the projected deductible impact ($150)…
        var deductible = result.AccumulatorSnapshot
            .First(s => s.Type == AccumulatorType.IndividualDeductible);
        Assert.Equal(150m, deductible.AmountApplied);

        // …but nothing was written back.
        Assert.Equal(0, acc.ApplyUpdatesCallCount);
    }

    [Fact]
    public async Task Prospective_DrgPath_DoesNotPersistAccumulators()
    {
        var plan = OfficeVisitPlan(inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var acc = new RecordingAccumulatorService();
        var engine = Engine(plan, acc, categoryCode: "48");

        var request = new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = plan.Id,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = NetworkTier.InNetwork,
            ClaimId = Guid.NewGuid().ToString(),
            ClaimType = "837I",
            DrgCode = "470",
            DrgAllowedAmount = 12000m,
            ExecutionMode = AdjudicationExecutionMode.Prospective,
            Lines =
            [
                new ClaimLineInput { LineNumber = 1, ProcedureCode = "99223", PlaceOfService = "21", BilledAmount = 12000m, Units = 1 }
            ],
            AllowedAmounts = new Dictionary<int, decimal> { [1] = 12000m }
        };

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.DrgCostShare);
        Assert.Equal(0, acc.ApplyUpdatesCallCount);
    }
}

/// <summary>
/// Accumulator double that records whether/what was written. It returns an
/// empty starting accumulator set; the engine's <c>AccumulatorWorkingSet</c>
/// seeds the standard embedded individual+family buckets from the plan config,
/// so tests exercise the real cost-sharing waterfall without pre-seeded state.
/// </summary>
internal sealed class RecordingAccumulatorService : IAccumulatorService
{
    public int ApplyUpdatesCallCount { get; private set; }
    public List<AccumulatorUpdate> CapturedUpdates { get; } = [];

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccumulatorSnapshot>>([]);

    public Task ApplyUpdatesAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear,
        string claimId, IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        ApplyUpdatesCallCount++;
        CapturedUpdates.AddRange(updates);
        return Task.CompletedTask;
    }

    public Task ReverseAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear,
        string claimId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct = default)
        => Task.CompletedTask;
}
