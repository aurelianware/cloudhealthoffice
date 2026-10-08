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
    public void Aggregate_AcaCapEnforcedWithoutCap_ZeroPlaceholderDoesNotCapToZero()
    {
        var ws = new AccumulatorWorkingSet(
            [ZeroPlaceholder(AccumulatorType.AcaIndividualCap, AccumulatorScope.Individual)],
            Plan(FamilyAccumulatorModel.Aggregate, famOop: 18_400m, acaEnforced: true));

        Assert.Equal(18_400m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }
}
