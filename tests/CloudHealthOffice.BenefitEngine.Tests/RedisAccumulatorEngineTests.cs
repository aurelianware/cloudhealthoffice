using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Testing.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// The benefit engine over <see cref="RedisAccumulatorService"/> (the store
/// benefit-plan-service runs with) on a real redis-server: Prospective
/// pricing, the prepared commit, and re-pricing / replacement afterwards —
/// both on a cache that holds the commit and on one rebuilt from
/// claims-service after an invalidation (per-claim journal records).
/// </summary>
public sealed class RedisAccumulatorEngineTests : IClassFixture<RedisServerFixture>
{
    private const string Member = "MBR-R1";
    private const string Subscriber = "SUB-R1";
    private readonly RedisServerFixture _redis;
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..8];
    private readonly ClaimsHistory _history = new();
    private readonly BenefitPlanConfig _plan = Plan();

    public RedisAccumulatorEngineTests(RedisServerFixture redis) => _redis = redis;

    private static BenefitPlanConfig Plan() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "test-tenant",
        PlanName = "Redis Plan",
        PlanType = PlanType.PPO,
        PlanYear = "2026",
        IndividualDeductible = 500,
        FamilyDeductible = 1500,
        IndividualOopMax = 3000,
        FamilyOopMax = 9000,
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
                    new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                ]
            },
        ]
    };

    private (BenefitCalculationEngine Engine, RedisAccumulatorService Store) Create()
    {
        Skip.If(_redis.Unavailable is not null, _redis.Unavailable);
        var store = new RedisAccumulatorService(_redis.Connection!, _history, new Tenant(_tenant),
            NullLogger<RedisAccumulatorService>.Instance);
        var engine = new BenefitCalculationEngine(
            new FixedCategoryResolver("98", "Office Visit"),
            new InMemoryBenefitPlanProvider(_plan),
            store,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);
        return (engine, store);
    }

    private BenefitResolutionRequest Request(string claimId, decimal allowed, string? replaces = null) => new()
    {
        MemberId = Member,
        SubscriberId = Subscriber,
        BenefitPlanId = _plan.Id,
        ServiceDate = new DateOnly(2026, 3, 8),
        NetworkTier = NetworkTier.InNetwork,
        ClaimId = claimId,
        ReplacesClaimId = replaces,
        ExecutionMode = AdjudicationExecutionMode.Prospective,
        Lines =
        [
            new ClaimLineInput { LineNumber = 1, ProcedureCode = "99213", PlaceOfService = "11", BilledAmount = allowed, Units = 1 }
        ],
        AllowedAmounts = new Dictionary<int, decimal> { [1] = allowed },
    };

    private async Task<decimal> Balance(IAccumulatorService store, AccumulatorType type)
    {
        var all = await store.GetAccumulatorsAsync(Member, Subscriber, _plan.Id, "2026");
        return all.Where(s => s.Type == type).Sum(s => s.AccumulatedAmountAfter);
    }

    private static async Task<BenefitResolutionResult> PriceAndCommit(BenefitCalculationEngine engine, BenefitResolutionRequest request)
    {
        var priced = await engine.CalculateAsync(request);
        Assert.True(priced.Success);
        Assert.NotNull(priced.PreparedAccumulatorCommit);
        var commit = await engine.CommitAccumulatorsAsync(priced.PreparedAccumulatorCommit!);
        Assert.Equal(AccumulatorCommitOutcome.Committed, commit.Outcome);
        return priced;
    }

    // ── B1: the commit journal answers GetClaimUpdatesAsync ─────────────

    [SkippableFact]
    public async Task TheCommitJournal_IsTheClaimsUpdates()
    {
        var (engine, store) = Create();
        var priced = await PriceAndCommit(engine, Request("C1", 200m));

        var updates = await store.GetClaimUpdatesAsync(Member, Subscriber, _plan.Id, "2026", "C1");

        var expected = priced.PreparedAccumulatorCommit!.Updates.Where(u => u.Amount > 0)
            .Select(u => (u.Type, u.Scope, u.NetworkTier, u.Amount)).OrderBy(x => x.ToString());
        Assert.Equal(expected, updates.Select(u => (u.Type, u.Scope, u.NetworkTier, u.Amount)).OrderBy(x => x.ToString()));
        Assert.Empty(await store.GetClaimUpdatesAsync(Member, Subscriber, _plan.Id, "2026", "NOT-THERE"));
    }

    [SkippableFact]
    public async Task RepricingACommittedClaim_GivesTheSameCostShare_AndRecommittingChangesNothing()
    {
        var (engine, store) = Create();
        var first = await PriceAndCommit(engine, Request("C1", 400m));
        Assert.Equal(400m, first.Totals.TotalDeductible);
        Assert.Equal(400m, await Balance(store, AccumulatorType.IndividualDeductible));

        // Re-adjudication of the same claim: its own committed write is not a
        // prior balance against itself.
        // (Counted against itself, only 100 of the deductible would be left.)
        var second = await PriceAndCommit(engine, Request("C1", 400m));

        Assert.Equal(first.Totals.TotalDeductible, second.Totals.TotalDeductible);
        Assert.Equal(first.Totals.TotalMemberResponsibility, second.Totals.TotalMemberResponsibility);
        Assert.Equal(400m, await Balance(store, AccumulatorType.IndividualDeductible));
        Assert.Equal(400m, await Balance(store, AccumulatorType.FamilyDeductible));
    }

    [SkippableFact]
    public async Task AReplacement_IsPricedWithoutTheOriginal_AndTakesItBack()
    {
        var (engine, store) = Create();
        await PriceAndCommit(engine, Request("C1", 400m));

        var replacement = await PriceAndCommit(engine, Request("C2", 300m, replaces: "C1"));

        Assert.Equal(300m, replacement.Totals.TotalDeductible);           // not 100 (against a 400 prior)
        Assert.Equal(300m, await Balance(store, AccumulatorType.IndividualDeductible));
        Assert.Equal(300m, await Balance(store, AccumulatorType.FamilyDeductible));
    }

    // ── B2: an invalidated cache rebuilt from claims-service journals per claim ──

    [SkippableFact]
    public async Task AfterInvalidateAndRebuild_AReplacementTakesTheOriginalBack()
    {
        var (engine, store) = Create();
        await PriceAndCommit(engine, Request("C1", 400m));
        _history.Approved("C1", deductible: 400m, oop: 400m);             // claims-service counts it now

        // Some other claim's reversal invalidates the cache; the next read rebuilds.
        await store.ReverseAsync(Member, Subscriber, _plan.Id, "2026", "OTHER");
        Assert.Equal(400m, await Balance(store, AccumulatorType.IndividualDeductible));

        var replacement = await PriceAndCommit(engine, Request("C2", 200m, replaces: "C1"));

        Assert.Equal(200m, replacement.Totals.TotalDeductible);
        Assert.Equal(200m, await Balance(store, AccumulatorType.IndividualDeductible));   // not 600
        Assert.Equal(200m, await Balance(store, AccumulatorType.FamilyDeductible));
    }

    [SkippableFact]
    public async Task AfterInvalidateAndRebuild_ARecommitOfTheSameClaimReplacesIt()
    {
        var (engine, store) = Create();
        await PriceAndCommit(engine, Request("C1", 400m));
        _history.Approved("C1", deductible: 400m, oop: 400m);
        await store.ReverseAsync(Member, Subscriber, _plan.Id, "2026", "OTHER");
        Assert.Equal(400m, await Balance(store, AccumulatorType.IndividualDeductible));

        var again = await PriceAndCommit(engine, Request("C1", 300m));     // corrected re-adjudication

        Assert.Equal(300m, again.Totals.TotalDeductible);                  // not 100 (against its own 400)
        Assert.Equal(300m, await Balance(store, AccumulatorType.IndividualDeductible));   // not 700
        Assert.Equal(300m, await Balance(store, AccumulatorType.FamilyDeductible));
    }

    [SkippableFact]
    public async Task AColdRebuild_JournalsEveryCountedClaim()
    {
        var (_, store) = Create();
        _history.Approved("C1", deductible: 100m, oop: 100m);
        _history.Approved("C2", deductible: 50m, oop: 80m);

        Assert.Equal(150m, await Balance(store, AccumulatorType.IndividualDeductible));

        var c2 = await store.GetClaimUpdatesAsync(Member, Subscriber, _plan.Id, "2026", "C2");
        Assert.Contains(c2, u => u.Type == AccumulatorType.IndividualDeductible && u.Amount == 50m);
        Assert.Contains(c2, u => u.Type == AccumulatorType.IndividualOutOfPocketMax && u.Amount == 80m);
        Assert.Contains(c2, u => u.Type == AccumulatorType.FamilyDeductible && u.Amount == 50m);
    }

    private sealed class Tenant(string id) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = id;
    }

    /// <summary>claims-service's accumulator-totals with per-claim contributions.</summary>
    private sealed class ClaimsHistory : IClaimsAccumulatorSource
    {
        private readonly List<(string Claim, decimal Deductible, decimal Oop)> _claims = [];

        public void Approved(string claim, decimal deductible, decimal oop) => _claims.Add((claim, deductible, oop));

        public async Task<(bool Success, IReadOnlyList<AccumulatorSnapshot> Snapshots)> CalculateAccumulatorsAsync(
            string tenantId, string ownerId, AccumulatorScope scope, Guid benefitPlanId, string planYear,
            CancellationToken ct = default)
        {
            var data = await CalculateAccumulatorsWithClaimsAsync(tenantId, ownerId, scope, benefitPlanId, planYear, ct);
            return (data.Success, data.Snapshots);
        }

        public Task<AccumulatorRebuildData> CalculateAccumulatorsWithClaimsAsync(
            string tenantId, string ownerId, AccumulatorScope scope, Guid benefitPlanId, string planYear,
            CancellationToken ct = default)
        {
            var (ded, oop) = scope == AccumulatorScope.Individual
                ? (AccumulatorType.IndividualDeductible, AccumulatorType.IndividualOutOfPocketMax)
                : (AccumulatorType.FamilyDeductible, AccumulatorType.FamilyOutOfPocketMax);
            AccumulatorUpdate U(AccumulatorType t, decimal a) => new()
            {
                Type = t, Scope = scope, NetworkTier = NetworkTier.InNetwork, Amount = a,
                Source = t == ded ? "Deductible" : "OOP",
            };
            var contributions = _claims
                .Select(c => new ClaimAccumulatorContribution(c.Claim, [U(ded, c.Deductible), U(oop, c.Oop)]))
                .ToList();
            IReadOnlyList<AccumulatorSnapshot> totals = _claims.Count == 0 ? [] :
            [
                new AccumulatorSnapshot { Type = ded, Scope = scope, NetworkTier = NetworkTier.InNetwork, AccumulatedAmountAfter = _claims.Sum(c => c.Deductible) },
                new AccumulatorSnapshot { Type = oop, Scope = scope, NetworkTier = NetworkTier.InNetwork, AccumulatedAmountAfter = _claims.Sum(c => c.Oop) },
            ];
            return Task.FromResult(new AccumulatorRebuildData(true, totals, contributions));
        }
    }
}
