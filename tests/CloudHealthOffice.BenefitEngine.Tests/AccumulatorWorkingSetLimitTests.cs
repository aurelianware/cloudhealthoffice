using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Null vs zero limit semantics in <see cref="AccumulatorWorkingSet"/>.
/// A limit the plan leaves unset (null) must never read as "already met":
/// before this, a plan with no family deductible / OOP max seeded a
/// zero-limit family accumulator, so the member owed nothing at all.
/// </summary>
public class AccumulatorWorkingSetLimitTests
{
    private static BenefitPlanConfig Plan(
        FamilyAccumulatorModel model = FamilyAccumulatorModel.Embedded,
        decimal? indDed = null, decimal? famDed = null,
        decimal? indOop = null, decimal? famOop = null,
        decimal? acaCap = null, bool acaEnforced = false) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-limits",
        PlanName = "Limits",
        PlanType = PlanType.PPO,
        FamilyAccumulatorModel = model,
        IndividualDeductible = indDed,
        FamilyDeductible = famDed,
        IndividualOopMax = indOop,
        FamilyOopMax = famOop,
        AcaIndividualCap = acaCap,
        IsAcaCapEnforced = acaEnforced,
    };

    private static AccumulatorSnapshot ZeroPlaceholder(AccumulatorType type, AccumulatorScope scope) => new()
    {
        Type = type,
        Scope = scope,
        NetworkTier = NetworkTier.InNetwork,
        LimitAmount = 0m,
        AccumulatedAmountAfter = 0m,
    };

    [Fact]
    public void Embedded_NullFamilyLimits_IndividualLimitsStillApply()
    {
        var ws = new AccumulatorWorkingSet([], Plan(indDed: 500m, indOop: 3_000m));

        Assert.Equal(500m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
        Assert.Equal(3_000m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
        // OON falls back to the in-network individual limits.
        Assert.Equal(500m, ws.GetRemainingDeductible(NetworkTier.OutOfNetwork));
    }

    [Fact]
    public void Embedded_NullFamilyLimits_ZeroLimitPlaceholdersDoNotReadAsMet()
    {
        var ws = new AccumulatorWorkingSet(
            [
                ZeroPlaceholder(AccumulatorType.FamilyDeductible, AccumulatorScope.Family),
                ZeroPlaceholder(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family),
            ],
            Plan(indDed: 500m, indOop: 3_000m));

        Assert.Equal(500m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
        Assert.Equal(3_000m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Embedded_FamilyCloserThanIndividual_FamilyBounds()
    {
        var ws = new AccumulatorWorkingSet(
            [
                new AccumulatorSnapshot
                {
                    Type = AccumulatorType.FamilyDeductible,
                    Scope = AccumulatorScope.Family,
                    NetworkTier = NetworkTier.InNetwork,
                    LimitAmount = 1_000m,
                    AccumulatedAmountAfter = 900m,
                },
            ],
            Plan(indDed: 500m, famDed: 1_000m, indOop: 3_000m, famOop: 6_000m));

        Assert.Equal(100m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
    }

    [Fact]
    public void ExplicitZeroDeductible_MeansNoDeductible()
    {
        var ws = new AccumulatorWorkingSet([], Plan(indDed: 0m, famDed: 0m, indOop: 3_000m));

        Assert.Equal(0m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
    }

    [Fact]
    public void NoDeductibleConfigured_MeansNoDeductible()
    {
        var ws = new AccumulatorWorkingSet([], Plan(indOop: 3_000m));

        Assert.Equal(0m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
    }

    [Fact]
    public void ZeroOopMax_TreatedAsUnset_NotAsMet()
    {
        var ws = new AccumulatorWorkingSet([], Plan(indDed: 500m, indOop: 0m, famOop: 0m));

        Assert.Equal(decimal.MaxValue, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_NullFamilyOop_Uncapped()
    {
        var ws = new AccumulatorWorkingSet([],
            Plan(FamilyAccumulatorModel.Aggregate, famDed: 1_000m));

        Assert.Equal(1_000m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
        Assert.Equal(decimal.MaxValue, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_NullFamilyOop_AcaIndividualCapStillApplies()
    {
        var ws = new AccumulatorWorkingSet(
            [ZeroPlaceholder(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family)],
            Plan(FamilyAccumulatorModel.Aggregate, acaCap: 9_200m, acaEnforced: true));

        Assert.Equal(9_200m, ws.GetRemainingOopMax(NetworkTier.InNetwork));

        ws.ApplyOopMax(9_200m, NetworkTier.InNetwork);
        Assert.Equal(0m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_NullFamilyLimits_IndividualLimitsArePool()
    {
        var ws = new AccumulatorWorkingSet(
            [
                ZeroPlaceholder(AccumulatorType.FamilyDeductible, AccumulatorScope.Family),
                ZeroPlaceholder(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family),
            ],
            Plan(FamilyAccumulatorModel.Aggregate, indDed: 1_000m, indOop: 4_000m));

        Assert.Equal(1_000m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
        Assert.Equal(4_000m, ws.GetRemainingOopMax(NetworkTier.InNetwork));

        ws.ApplyDeductible(600m, NetworkTier.InNetwork);
        ws.ApplyOopMax(600m, NetworkTier.InNetwork);

        Assert.Equal(400m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
        Assert.Equal(3_400m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
        Assert.Contains(ws.GetPendingUpdates(), u =>
            u.Type == AccumulatorType.IndividualDeductible && u.Amount == 600m);
    }

    [Fact]
    public void Aggregate_IndividualFallback_EachAmountRecordedOnceInTheActivePool()
    {
        // Zero-limit family placeholders (as Redis/CHO sources emit) plus the
        // individual fallback pool: one application must land in exactly one
        // deductible accumulator and one OOP accumulator (+ the ACA cap).
        var ws = new AccumulatorWorkingSet(
            [
                ZeroPlaceholder(AccumulatorType.FamilyDeductible, AccumulatorScope.Family),
                ZeroPlaceholder(AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family),
            ],
            Plan(FamilyAccumulatorModel.Aggregate, indDed: 1_000m, indOop: 4_000m,
                acaCap: 9_200m, acaEnforced: true));

        ws.ApplyDeductible(250m, NetworkTier.InNetwork);
        ws.ApplyOopMax(250m, NetworkTier.InNetwork);

        var updates = ws.GetPendingUpdates();
        Assert.Single(updates, u => u.Type is AccumulatorType.IndividualDeductible or AccumulatorType.FamilyDeductible);
        Assert.Single(updates, u => u.Type is AccumulatorType.IndividualOutOfPocketMax or AccumulatorType.FamilyOutOfPocketMax);
        Assert.Single(updates, u => u.Type == AccumulatorType.AcaIndividualCap);
        Assert.All(updates, u => Assert.Equal(250m, u.Amount));

        var snapshot = ws.GetSnapshot().Where(s => s.NetworkTier == NetworkTier.InNetwork).ToList();
        Assert.Equal(250m, snapshot.Single(s => s.Type == AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(0m, snapshot.Single(s => s.Type == AccumulatorType.FamilyDeductible).AmountApplied);
    }

    [Fact]
    public void Aggregate_FamilyConfigured_IndividualLimitIgnored()
    {
        var ws = new AccumulatorWorkingSet([],
            Plan(FamilyAccumulatorModel.Aggregate, indDed: 1_000m, famDed: 3_000m,
                indOop: 4_000m, famOop: 8_000m));

        Assert.Equal(3_000m, ws.GetRemainingDeductible(NetworkTier.InNetwork));
        Assert.Equal(8_000m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_NullFamilyOop_IndividualOopAndAcaCap_TighterWins()
    {
        var ws = new AccumulatorWorkingSet([],
            Plan(FamilyAccumulatorModel.Aggregate, indOop: 12_000m, acaCap: 9_200m, acaEnforced: true));

        Assert.Equal(9_200m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_AcaCapEnforcedWithoutCap_ZeroPlaceholderDoesNotCapToZero()
    {
        var ws = new AccumulatorWorkingSet(
            [ZeroPlaceholder(AccumulatorType.AcaIndividualCap, AccumulatorScope.Individual)],
            Plan(FamilyAccumulatorModel.Aggregate, famOop: 18_400m, acaEnforced: true));

        Assert.Equal(18_400m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }
}
