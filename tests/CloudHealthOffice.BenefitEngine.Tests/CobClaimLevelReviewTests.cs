using System.Text.Json;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.CobEngine.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// PR #1278 review fixes on the engine side: a prior payer's denial does
/// not bound our payment (B1), re-adjudication / void / replacement keep
/// the accumulators right (M2), and the store clamps a deductible credit at
/// the limit at write time (concurrent claims).
/// </summary>
public partial class BenefitCalculationEngineTests
{
    // Inpatient category: deductible, then 20% coinsurance. Deductible met:
    // allowed $300 → cost share $60, normal benefit $240.

    /// <summary>
    /// B1. The primary denied the service: AMT*D $0 and only a CO adjustment
    /// (no PR). Its "PR = 0" is not what the member owes after it, so it
    /// does not bound our payment: balance = allowed − prior paid = $300, we
    /// pay our normal $240 and the member owes our $60 coinsurance — not $0
    /// to the provider. Same with the denial reported at claim level (2320)
    /// or line level (2430).
    /// </summary>
    [Theory]
    [InlineData("27", false)]   // expenses incurred after coverage terminated
    [InlineData("22", false)]   // may be covered by another payer per COB
    [InlineData("109", false)]  // not covered by this payer/contractor
    [InlineData("96", false)]   // non-covered charge(s)
    [InlineData("204", false)]  // not covered under the patient's current benefit plan
    [InlineData("27", true)]
    [InlineData("22", true)]
    [InlineData("109", true)]
    [InlineData("96", true)]
    [InlineData("204", true)]
    public async Task Cob_PriorPayerDenial_DoesNotBoundOurPayment(string carc, bool lineLevel)
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "48", existingDeductible: 500m);
        var primary = lineLevel
            ? PriorPayer(1, claimPaid: 0m, lines: (1, 0m, [("CO", carc, 500m)]))
            : PriorPayer(1, claimPaid: 0m, claimCas: [("CO", carc, 500m)]);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: ("99223", 500m, 300m, "21")), ourSequence: 2, complementary: true, primary);

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(240m, line.PlanPaidAmount);
        Assert.Equal(60m, line.MemberResponsibility);
        Assert.Equal(new[] { ("CO", "45", 200m), ("PR", "2", 60m) }, Cas(line));
        AssertCasInvariants(line);
    }

    /// <summary>
    /// B1, tertiary: the secondary denied (CO-109, $0, no PR); the primary
    /// paid $100 and left PR-2 $50. The bound is the PR of the last payer
    /// that adjudicated — the primary's $50 — not the secondary's "$0":
    /// balance = min(300 − 100, 50) = $50; we pay $50, member $0, OA-23 $250.
    /// </summary>
    [Fact]
    public async Task Cob_Tertiary_SecondaryDenied_BoundIsPrimaryPr()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "48", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: ("99223", 500m, 300m, "21")), ourSequence: 3, complementary: true,
            PriorPayer(1, claimPaid: 100m, lines: (1, 100m, [("CO", "45", 350m), ("PR", "2", 50m)])),
            PriorPayer(2, claimPaid: 0m, claimCas: [("CO", "109", 500m)]));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(50m, line.PlanPaidAmount);
        Assert.Equal(0m, line.MemberResponsibility);
        Assert.Equal(250m, Oa23(line.Adjustments));
        AssertCasInvariants(line);
    }

    /// <summary>
    /// B1 mixed: the primary paid line 1 ($200, PR-2 $50) and denied line 2
    /// (2430 SVD $0, CO-27 only). Line 1 is bounded by the primary's $50 PR
    /// (room 100); line 2 by nothing (room 300). Claim balance $350 ≤ our
    /// normal $480 → we pay $350 and the member owes $0: what the member
    /// still owed after the primary (L1 $50 + L2 $300) is paid. Distributed
    /// within each line's balance: L1 $50, L2 $300.
    /// </summary>
    [Fact]
    public async Task Cob_MixedPaidAndDeniedLines_DenialUnbounded()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 6000);
        var engine = CreateEngine(plan, categoryCode: "48", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: [("99223", 500m, 300m, "21"), ("99233", 500m, 300m, "21")]),
            ourSequence: 2, complementary: true,
            PriorPayer(1, claimPaid: 200m, lines:
            [
                (1, 200m, [("CO", "45", 250m), ("PR", "2", 50m)]),
                (2, 0m, [("CO", "27", 500m)]),
            ]));

        var result = await engine.CalculateAsync(request);

        Assert.Equal(new[] { 50m, 300m }, result.Lines.Select(l => l.PlanPaidAmount));
        Assert.Equal(new[] { 0m, 0m }, result.Lines.Select(l => l.MemberResponsibility));
        Assert.Equal(new[] { 250m, 0m }, result.Lines.Select(l => Oa23(l.Adjustments)));
        Assert.Equal(350m, result.Totals.TotalPlanPaid);
        Assert.All(result.Lines, AssertCasInvariants);
    }

    // ═══════════════════════════════════════════════════════════════════
    // M2 — re-adjudication, void and replacement against the real store
    // ═══════════════════════════════════════════════════════════════════

    private static (BenefitCalculationEngine Engine, InMemoryAccumulatorDocuments Store, IAccumulatorService Accumulators)
        ChoEngine(BenefitPlanConfig plan, string categoryCode)
    {
        var store = new InMemoryAccumulatorDocuments();
        var accumulators = new ChoAccumulatorService(store, new FixedTenant(), NullLogger<ChoAccumulatorService>.Instance);
        var engine = new BenefitCalculationEngine(
            new FixedCategoryResolver(categoryCode, plan.GetFirstCategory(categoryCode)?.ServiceTypeDescription ?? "x"),
            new InMemoryBenefitPlanProvider(plan),
            accumulators,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);
        return (engine, store, accumulators);
    }

    /// <summary>
    /// Adjudicating the same claim twice (re-adjudication) gives the same
    /// result and leaves the accumulators as one adjudication would: the
    /// second pass is priced without the claim's own first-pass updates
    /// (otherwise it would meet its own deductible and pay $96 more), and
    /// its updates replace the first pass's instead of being skipped.
    /// </summary>
    [Fact]
    public async Task Readjudication_SameClaim_SamePaymentAndAccumulators()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var (engine, store, _) = ChoEngine(plan, "98");
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var first = await engine.CalculateAsync(request);
        var second = await engine.CalculateAsync(request);

        Assert.Equal(150m, first.Lines[0].DeductibleAmount);
        Assert.Equal(Cas(first.Lines[0]), Cas(second.Lines[0]));
        Assert.Equal(first.Totals.TotalPlanPaid, second.Totals.TotalPlanPaid);
        Assert.Equal(150m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        Assert.Equal(150m, store.Balance("SUB-001", AccumulatorType.FamilyDeductible));
        Assert.Equal(150m, store.Balance("MBR-001", AccumulatorType.IndividualOutOfPocketMax));
    }

    /// <summary>
    /// A void reverses the claim's accumulators, and a replacement — the
    /// original reversed, the corrected claim adjudicated under a new id —
    /// does not count the deductible twice.
    /// </summary>
    [Fact]
    public async Task VoidAndReplacement_DoNotDoubleCountTheDeductible()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var (engine, store, _) = ChoEngine(plan, "98");
        var original = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));
        await engine.CalculateAsync(original);
        Assert.Equal(150m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));

        // Void.
        await engine.ReverseClaimAsync("MBR-001", "SUB-001", plan.Id, original.ServiceDate, original.ClaimId);
        Assert.Equal(0m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        Assert.Equal(0m, store.Balance("MBR-001", AccumulatorType.IndividualOutOfPocketMax));

        // Replacement: the original applied again (re-adjudicated) …
        await engine.CalculateAsync(original);
        Assert.Equal(150m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        // … then superseded: original reversed, replacement applied.
        var replacement = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));
        await engine.ReverseClaimAsync("MBR-001", "SUB-001", plan.Id, original.ServiceDate, original.ClaimId);
        var replaced = await engine.CalculateAsync(replacement);
        Assert.Equal(150m, replaced.Lines[0].DeductibleAmount);
        Assert.Equal(150m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Re-review N6 — a replacement is priced without its predecessor
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The original met the whole $500 deductible (allowed $900: deductible
    /// $500 + 20% × 400 = $80 → pays $320). An identical replacement is
    /// priced without the original's updates, so it pays the same $320 — not
    /// the $720 of a claim whose deductible is already met — and writing it
    /// reverses the original's updates. The original's later void reverses
    /// nothing again: the deductible stays $500.
    /// </summary>
    [Fact]
    public async Task Replacement_PricedWithoutThePredecessor_AndTheVoidDoesNotDoubleReverse()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 6000);
        var (engine, store, _) = ChoEngine(plan, "48");
        var original = CreateRequest(plan.Id, lines: ("99223", 1000m, 900m, "21")) with { ClaimId = "C1" };

        var first = await engine.CalculateAsync(original);
        Assert.Equal(320m, first.Totals.TotalPlanPaid);
        Assert.Equal(500m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));

        var replacement = original with { ClaimId = "C2", ReplacesClaimId = "C1" };
        var second = await engine.CalculateAsync(replacement);

        Assert.Equal(320m, second.Totals.TotalPlanPaid);
        Assert.Equal(500m, second.Lines[0].DeductibleAmount);
        Assert.Equal(500m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        Assert.Equal(580m, store.Balance("MBR-001", AccumulatorType.IndividualOutOfPocketMax));

        // The reversal run voids the superseded original.
        await engine.ReverseClaimAsync("MBR-001", "SUB-001", plan.Id, original.ServiceDate, "C1");
        Assert.Equal(500m, store.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        Assert.Equal(580m, store.Balance("MBR-001", AccumulatorType.IndividualOutOfPocketMax));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Settings — the result must not depend on how services split into claims
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitPlanConfig DeductibleOnlyPlan(CobDeductibleCredit credit) =>
        CreateTestPlan(individualDeductible: 500, individualOopMax: 6000) with
        {
            CobDeductibleCredit = credit,
            Categories =
            [
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "DED",
                    ServiceTypeDescription = "Deductible, then 100%",
                    IsCovered = true,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                    ],
                },
            ],
        };

    /// <summary>
    /// Reviewer's example. Two services, allowed $500 each, $500 of the
    /// deductible left; we are secondary. The primary paid L1 $500 (PR 0)
    /// and L2 $0 (PR-1 $500). Run once as one two-line claim and once as two
    /// one-line claims (accumulators carried over):
    /// <list type="bullet">
    ///   <item><description>NaicFullCredit: L1 meets the deductible as the only
    ///     plan would (credited $500 though the member owes nothing); L2 is
    ///     then paid in full — $500 either way.</description></item>
    ///   <item><description>MemberPaidOnly: L1 credits only what the member
    ///     pays ($0), so L2 still meets the deductible: the member owes $500,
    ///     we pay $0 — either way. (Pricing the claim "as the only plan" first
    ///     would pay $500 on one claim but $0 split across two.)</description></item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(CobDeductibleCredit.NaicFullCredit, 500, 0)]
    [InlineData(CobDeductibleCredit.MemberPaidOnly, 0, 500)]
    public async Task ResultDoesNotDependOnClaimSplitting(CobDeductibleCredit credit, decimal expectedPaid, decimal expectedMember)
    {
        var plan = DeductibleOnlyPlan(credit);
        PriorPayerAdjudication Primary(params (int line, decimal paid, (string, string, decimal)[] cas)[] lines) =>
            PriorPayer(1, claimPaid: lines.Sum(l => l.paid), lines: lines);
        (int, decimal, (string, string, decimal)[]) L1(int n) => (n, 500m, [("CO", "45", 100m)]);
        (int, decimal, (string, string, decimal)[]) L2(int n) => (n, 0m, [("CO", "45", 100m), ("PR", "1", 500m)]);

        // One claim.
        var (oneEngine, oneStore, _) = ChoEngine(plan, "DED");
        var one = await oneEngine.CalculateAsync(WithPriorPayers(
            CreateRequest(plan.Id, lines: [("S1", 500m, 500m, "11"), ("S2", 500m, 500m, "11")]),
            ourSequence: 2, complementary: true, Primary(L1(1), L2(2))));

        // Two claims.
        var (twoEngine, twoStore, _) = ChoEngine(plan, "DED");
        var a = await twoEngine.CalculateAsync(WithPriorPayers(
            CreateRequest(plan.Id, lines: ("S1", 500m, 500m, "11")), 2, true, Primary(L1(1))));
        var b = await twoEngine.CalculateAsync(WithPriorPayers(
            CreateRequest(plan.Id, lines: ("S2", 500m, 500m, "11")), 2, true, Primary(L2(1))));

        Assert.Equal(expectedPaid, one.Totals.TotalPlanPaid);
        Assert.Equal(expectedPaid, a.Totals.TotalPlanPaid + b.Totals.TotalPlanPaid);
        Assert.Equal(expectedMember, one.Totals.TotalMemberResponsibility);
        Assert.Equal(expectedMember, a.Totals.TotalMemberResponsibility + b.Totals.TotalMemberResponsibility);
        Assert.Equal(oneStore.Balance("MBR-001", AccumulatorType.IndividualDeductible),
            twoStore.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        Assert.Equal(500m, oneStore.Balance("MBR-001", AccumulatorType.IndividualDeductible));
        Assert.All(one.Lines.Concat(a.Lines).Concat(b.Lines), AssertCasInvariants);
    }

    /// <summary>
    /// Two claims priced against the same starting balance ($100 of the
    /// deductible left), each crediting $100: the store clamps at the plan
    /// limit when it writes, so together they credit $100, not $200.
    /// </summary>
    [Fact]
    public async Task Store_ClampsDeductibleCreditAtTheLimit_AtWriteTime()
    {
        var store = new InMemoryAccumulatorDocuments();
        var accumulators = new ChoAccumulatorService(store, new FixedTenant(), NullLogger<ChoAccumulatorService>.Instance);
        var planId = Guid.NewGuid();
        AccumulatorUpdate Deductible(decimal amount) => new()
        {
            Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
            NetworkTier = NetworkTier.InNetwork, Amount = amount, Source = "Deductible", ClampAtLimit = 500m,
        };

        await accumulators.ApplyUpdatesAsync("M", "S", planId, "2026", "seed", [Deductible(400m)]);
        await accumulators.ApplyUpdatesAsync("M", "S", planId, "2026", "claim-a", [Deductible(100m)]);
        await accumulators.ApplyUpdatesAsync("M", "S", planId, "2026", "claim-b", [Deductible(100m)]);

        Assert.Equal(500m, store.Balance("M", AccumulatorType.IndividualDeductible));
        var claimB = await accumulators.GetClaimUpdatesAsync("M", "S", planId, "2026", "claim-b");
        Assert.Equal(0m, Assert.Single(claimB).Amount);
    }

    /// <summary>
    /// The clamp also holds across an optimistic-concurrency conflict: the
    /// write that loses the race reloads the document and re-clamps against
    /// the balance the winner left.
    /// </summary>
    [Fact]
    public async Task Store_ConcurrencyConflict_ReloadsAndReclamps()
    {
        var store = new InMemoryAccumulatorDocuments();
        var accumulators = new ChoAccumulatorService(store, new FixedTenant(), NullLogger<ChoAccumulatorService>.Instance);
        var planId = Guid.NewGuid();
        AccumulatorUpdate Deductible(decimal amount) => new()
        {
            Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
            NetworkTier = NetworkTier.InNetwork, Amount = amount, Source = "Deductible", ClampAtLimit = 500m,
        };
        await accumulators.ApplyUpdatesAsync("M", "S", planId, "2026", "seed", [Deductible(400m)]);

        // Claim A writes while claim B is between its read and its write.
        store.BeforeNextUpsert = () =>
            accumulators.ApplyUpdatesAsync("M", "S", planId, "2026", "claim-a", [Deductible(100m)]).GetAwaiter().GetResult();
        await accumulators.ApplyUpdatesAsync("M", "S", planId, "2026", "claim-b", [Deductible(100m)]);

        Assert.Equal(500m, store.Balance("M", AccumulatorType.IndividualDeductible));
    }
}

internal sealed class FixedTenant : IBenefitEngineTenantContext
{
    public string TenantId => "tenant-1";
}

/// <summary>Accumulator documents in memory, with the version check the real stores enforce.</summary>
internal sealed class InMemoryAccumulatorDocuments : IAccumulatorRepository
{
    private readonly Dictionary<string, string> _docs = new();

    /// <summary>Runs once, just before the next upsert is checked (to simulate a concurrent writer).</summary>
    public Action? BeforeNextUpsert { get; set; }

    public Task<AccumulatorDocument?> GetAsync(string tenantId, string ownerId, AccumulatorScope scope,
        Guid benefitPlanId, string planYear, CancellationToken ct = default)
    {
        var id = AccumulatorDocument.MakeId(tenantId, scope.ToString(), ownerId, benefitPlanId, planYear);
        return Task.FromResult(_docs.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<AccumulatorDocument>(json) : null);
    }

    public Task<AccumulatorDocument> UpsertAsync(AccumulatorDocument document, CancellationToken ct = default)
    {
        var hook = BeforeNextUpsert;
        BeforeNextUpsert = null;
        hook?.Invoke();

        var storedVersion = _docs.TryGetValue(document.Id, out var json)
            ? JsonSerializer.Deserialize<AccumulatorDocument>(json)!.Version
            : 0;
        if (storedVersion != document.Version)
            throw new OptimisticConcurrencyException(document.Id);
        document.Version += 1;
        _docs[document.Id] = JsonSerializer.Serialize(document);
        return Task.FromResult(document);
    }

    public Task DeleteByPlanYearAsync(string tenantId, Guid benefitPlanId, string planYear, CancellationToken ct = default)
    {
        _docs.Clear();
        return Task.CompletedTask;
    }

    public decimal Balance(string ownerId, AccumulatorType type) =>
        _docs.Values
            .Select(j => JsonSerializer.Deserialize<AccumulatorDocument>(j)!)
            .Where(d => d.OwnerId == ownerId)
            .SelectMany(d => d.Balances)
            .Where(b => b.Type == type.ToString() && b.NetworkTier == nameof(NetworkTier.InNetwork))
            .Sum(b => b.AccumulatedAmount);
}
