using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// The Pay/Deny lifecycle on the engine: a Pay claim moved to Deny before the payment cycle
/// is reversed NON-terminally (no fence), so its later re-commit (a new commit id) is
/// accepted and counted once; a void stays terminal (fenced, a later commit refused).
/// </summary>
public class ReprocessingReversalTests
{
    private static readonly BenefitPlanConfig Plan = new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-1",
        PlanName = "Reprocessing plan",
        PlanType = PlanType.PPO,
        PlanYear = "2026",
        IndividualDeductible = 500,
        FamilyDeductible = 1000,
        IndividualOopMax = 3000,
        FamilyOopMax = 6000,
        Categories = [],
    };

    private readonly InMemoryAccumulatorDocuments _store = new();
    private readonly ChoAccumulatorService _accumulators;
    private readonly BenefitCalculationEngine _engine;

    public ReprocessingReversalTests()
    {
        _accumulators = new ChoAccumulatorService(_store, new FixedTenant(), NullLogger<ChoAccumulatorService>.Instance);
        _engine = new BenefitCalculationEngine(
            new ServiceCategoryResolver(new EmptyRepo(), NullLogger<ServiceCategoryResolver>.Instance),
            new InMemoryBenefitPlanProvider(Plan),
            _accumulators,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);
    }

    private static AccumulatorCommit Commit(string claimId) => new()
    {
        CommitId = Guid.NewGuid().ToString("N"),
        ClaimId = claimId,
        MemberId = "MBR-1",
        SubscriberId = "MBR-1",
        BenefitPlanId = Plan.Id,
        PlanYear = "2026",
        Updates =
        [
            new AccumulatorUpdate
            {
                Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Amount = 100m, Source = "Deductible", ClampAtLimit = 500m,
            },
        ],
    };

    private async Task<decimal> DeductibleAsync() =>
        (await _accumulators.GetAccumulatorsAsync("MBR-1", "MBR-1", Plan.Id, "2026"))
        .Where(a => a.Type == AccumulatorType.IndividualDeductible).Sum(a => a.AccumulatedAmountAfter);

    private static readonly DateOnly ServiceDate = new(2026, 4, 15);

    private Task<CloudHealthOffice.BenefitEngine.Persistence.AccumulatorDocument?> IndividualDocAsync() =>
        _store.GetAsync("tenant-1", "MBR-1", AccumulatorScope.Individual, Plan.Id, "2026");

    private sealed class EmptyRepo : IServiceCategoryMappingRepository
    {
        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ServiceCategoryMapping>>(Array.Empty<ServiceCategoryMapping>());
    }

    [Fact]
    public async Task PayDenyPay_NonTerminalReversal_ThenRecommit_CountsOnce()
    {
        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(Commit("C1"))).Outcome);
        Assert.Equal(100m, await DeductibleAsync());

        await _engine.ReverseClaimForReprocessingAsync("MBR-1", "MBR-1", Plan.Id, ServiceDate, "C1");
        Assert.Equal(0m, await DeductibleAsync());
        Assert.DoesNotContain("C1", (await IndividualDocAsync())!.ReversedClaimIds);

        var again = await _engine.CommitAccumulatorsAsync(Commit("C1"));

        Assert.Equal(AccumulatorCommitOutcome.Committed, again.Outcome);
        Assert.Empty(again.Clamped);
        Assert.Equal(100m, await DeductibleAsync());
    }

    [Fact]
    public async Task NonTerminalReversal_OfANeverCommittedClaim_ChangesNothing()
    {
        await _engine.ReverseClaimForReprocessingAsync("MBR-1", "MBR-1", Plan.Id, ServiceDate, "NEVER");

        Assert.Equal(0m, await DeductibleAsync());
        Assert.DoesNotContain("NEVER", (await IndividualDocAsync())?.ReversedClaimIds ?? []);
    }

    [Fact]
    public async Task VoidAfterRecommit_IsStillTerminal()
    {
        await _engine.CommitAccumulatorsAsync(Commit("C2"));
        await _engine.ReverseClaimForReprocessingAsync("MBR-1", "MBR-1", Plan.Id, ServiceDate, "C2");
        await _engine.CommitAccumulatorsAsync(Commit("C2"));
        Assert.Equal(100m, await DeductibleAsync());

        await _engine.ReverseClaimAsync("MBR-1", "MBR-1", Plan.Id, ServiceDate, "C2");

        Assert.Equal(0m, await DeductibleAsync());
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _engine.CommitAccumulatorsAsync(Commit("C2"))).Outcome);
        Assert.Equal(0m, await DeductibleAsync());
    }
}
