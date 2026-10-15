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

    private Task<AccumulatorReversalOutcome> ReverseForDeny(string claimId, string? commitId) =>
        _engine.ReverseClaimForReprocessingAsync("MBR-1", "MBR-1", Plan.Id, ServiceDate, claimId, commitId);

    [Fact]
    public async Task PayDenyPay_CommitScopedReversal_ThenRecommit_CountsOnce()
    {
        var first = Commit("C1");
        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(first)).Outcome);
        Assert.Equal(100m, await DeductibleAsync());

        Assert.Equal(AccumulatorReversalOutcome.Reversed, await ReverseForDeny("C1", first.CommitId));
        Assert.Equal(0m, await DeductibleAsync());
        Assert.DoesNotContain("C1", (await IndividualDocAsync())!.ReversedClaimIds);

        var again = await _engine.CommitAccumulatorsAsync(Commit("C1"));

        Assert.Equal(AccumulatorCommitOutcome.Committed, again.Outcome);
        Assert.Empty(again.Clamped);
        Assert.Equal(100m, await DeductibleAsync());
    }

    /// <summary>Interleaving: the Deny's reversal is dispatched after the re-Pay commit landed — a no-op.</summary>
    [Fact]
    public async Task StaleReversal_AfterTheRePayCommit_IsSuperseded()
    {
        var first = Commit("C3");
        await _engine.CommitAccumulatorsAsync(first);
        var second = Commit("C3");
        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(second)).Outcome);

        Assert.Equal(AccumulatorReversalOutcome.Superseded, await ReverseForDeny("C3", first.CommitId));

        Assert.Equal(100m, await DeductibleAsync());
        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, (await _engine.CommitAccumulatorsAsync(second)).Outcome);
    }

    /// <summary>Interleaving: the Pay commit is dispatched after the Deny's reversal — refused, nothing counted; a newer commit is not.</summary>
    [Fact]
    public async Task LateCommit_AfterTheDenyReversal_IsRefused_ANewerCommitIsNot()
    {
        var inFlight = Commit("C4");
        Assert.Equal(AccumulatorReversalOutcome.Reversed, await ReverseForDeny("C4", inFlight.CommitId));

        Assert.Equal(AccumulatorCommitOutcome.RefusedCommitReversed, (await _engine.CommitAccumulatorsAsync(inFlight)).Outcome);
        Assert.Equal(0m, await DeductibleAsync());

        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(Commit("C4"))).Outcome);
        Assert.Equal(100m, await DeductibleAsync());
    }

    /// <summary>Legacy sentinel: the commit is unknown — a blind reversal, nothing fenced.</summary>
    [Fact]
    public async Task LegacyUnknownCommit_ReversesBlind_FencesNothing()
    {
        var legacy = Commit("C5");
        await _engine.CommitAccumulatorsAsync(legacy);

        Assert.Equal(AccumulatorReversalOutcome.Reversed, await ReverseForDeny("C5", null));

        Assert.Equal(0m, await DeductibleAsync());
        var doc = (await IndividualDocAsync())!;
        Assert.Empty(doc.ReversedCommitIds);
        Assert.DoesNotContain("C5", doc.ReversedClaimIds);
        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _engine.CommitAccumulatorsAsync(Commit("C5"))).Outcome);
    }

    [Fact]
    public async Task NonTerminalReversal_OfANeverCommittedClaim_ChangesNothing()
    {
        await ReverseForDeny("NEVER", null);

        Assert.Equal(0m, await DeductibleAsync());
        Assert.DoesNotContain("NEVER", (await IndividualDocAsync())?.ReversedClaimIds ?? []);
    }

    [Fact]
    public async Task VoidAfterRecommit_IsStillTerminal()
    {
        var first = Commit("C2");
        await _engine.CommitAccumulatorsAsync(first);
        await ReverseForDeny("C2", first.CommitId);
        await _engine.CommitAccumulatorsAsync(Commit("C2"));
        Assert.Equal(100m, await DeductibleAsync());

        await _engine.ReverseClaimAsync("MBR-1", "MBR-1", Plan.Id, ServiceDate, "C2");

        Assert.Equal(0m, await DeductibleAsync());
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _engine.CommitAccumulatorsAsync(Commit("C2"))).Outcome);
        Assert.Equal(0m, await DeductibleAsync());
    }
}
