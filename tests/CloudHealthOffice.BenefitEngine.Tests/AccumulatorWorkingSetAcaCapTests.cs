using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Aggregate-mode ACA 45 CFR §156.130 individual-cap enforcement
/// (capability BP 5.7). Exercises <see cref="AccumulatorWorkingSet"/>
/// directly so the assertions don't depend on the broader engine wiring.
///
/// <para>
/// Existing Aggregate tests in <see cref="BenefitCalculationEngineTests"/>
/// stay unchanged: they use dollar amounts below the 2025 ACA cap of
/// $9,200, so cap enforcement is a no-op for them. These tests pick
/// scenarios where the cap actually bites.
/// </para>
/// </summary>
public class AccumulatorWorkingSetAcaCapTests
{
    private const decimal AcaIndividualCap2025 = 9_200m;
    private const decimal AcaFamilyCap2025 = 18_400m;

    private static BenefitPlanConfig AggregatePlan(
        decimal familyOop = AcaFamilyCap2025,
        decimal? acaCap = AcaIndividualCap2025,
        bool enforced = true) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-aca",
        PlanName = "Aggregate ACA",
        PlanType = PlanType.HDHP,
        FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate,
        FamilyOopMax = familyOop,
        AcaIndividualCap = acaCap,
        IsAcaCapEnforced = enforced,
    };

    [Fact]
    public void Aggregate_AcaCapEnforced_RemainingOopMax_IsMinOfFamilyAndCap()
    {
        var plan = AggregatePlan();
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        // Empty pool: family $18,400 — cap $9,200 → min is the cap.
        Assert.Equal(AcaIndividualCap2025, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_AcaCapEnforced_MemberContributionLimitedByCap()
    {
        var plan = AggregatePlan();
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        // Member responsibility on a single $20,000 claim — engine bounds
        // application by remaining-oop-max which is min(family, cap) =
        // $9,200. Apply that and re-check.
        ws.ApplyOopMax(AcaIndividualCap2025, NetworkTier.InNetwork);

        // After the cap is hit, this member can absorb $0 more even
        // though the family pool still has $9,200 of headroom.
        Assert.Equal(0m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Aggregate_AcaCapEnforced_FamilyPoolAccumulatesAlongsideCap()
    {
        var plan = AggregatePlan();
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        ws.ApplyOopMax(5_000m, NetworkTier.InNetwork);

        // Family pool: 18,400 - 5,000 = 13,400 remaining.
        // ACA cap: 9,200 - 5,000 = 4,200 remaining.
        // Member sees min = 4,200.
        Assert.Equal(4_200m, ws.GetRemainingOopMax(NetworkTier.InNetwork));

        // Snapshot exposes both buckets — the family pool entry shows
        // 5,000 applied, the AcaIndividualCap entry shows 5,000 applied.
        var snap = ws.GetSnapshot();
        var family = snap.Single(e =>
            e.Type == AccumulatorType.FamilyOutOfPocketMax &&
            e.NetworkTier == NetworkTier.InNetwork);
        var cap = snap.Single(e =>
            e.Type == AccumulatorType.AcaIndividualCap &&
            e.NetworkTier == NetworkTier.InNetwork);
        Assert.Equal(5_000m, family.AmountApplied);
        Assert.Equal(5_000m, cap.AmountApplied);
    }

    [Fact]
    public void Aggregate_AcaCapEnforced_PendingUpdatesIncludeCapEntry()
    {
        var plan = AggregatePlan();
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        ws.ApplyOopMax(2_500m, NetworkTier.InNetwork);
        var updates = ws.GetPendingUpdates();

        // Two updates: family-aggregate + aca-individual-cap.
        Assert.Contains(updates, u =>
            u.Type == AccumulatorType.FamilyOutOfPocketMax &&
            u.Source == "OOP-Family-Aggregate" &&
            u.Amount == 2_500m);
        Assert.Contains(updates, u =>
            u.Type == AccumulatorType.AcaIndividualCap &&
            u.Source == "OOP-AcaIndividualCap" &&
            u.Amount == 2_500m);
    }

    [Fact]
    public void Aggregate_AcaCapNotEnforced_RemainingOopMax_IsFamilyOnly()
    {
        // Legacy Aggregate plan (G8 gated rollout): IsAcaCapEnforced=false.
        // Behavior must match pre-5.7 — full family pool is available to
        // any single member; no per-member cap.
        var plan = AggregatePlan(enforced: false);
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        Assert.Equal(AcaFamilyCap2025, ws.GetRemainingOopMax(NetworkTier.InNetwork));

        ws.ApplyOopMax(15_000m, NetworkTier.InNetwork);

        // Family pool drops by 15,000; no ACA cap enforcement.
        Assert.Equal(3_400m, ws.GetRemainingOopMax(NetworkTier.InNetwork));

        // Snapshot does NOT include an AcaIndividualCap entry.
        var snap = ws.GetSnapshot();
        Assert.DoesNotContain(snap, e => e.Type == AccumulatorType.AcaIndividualCap);
    }

    [Fact]
    public void Aggregate_AcaCapEnforced_NullCap_FallsBackToFamilyOnly()
    {
        // When IAcaLimitsProvider returns null (plan-year not configured)
        // the mapper sets AcaIndividualCap to null. Even with
        // IsAcaCapEnforced=true the working set then degrades gracefully
        // to family-only behavior — runtime never crashes; the validator
        // already rejected the plan at write time.
        var plan = AggregatePlan(acaCap: null);
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        Assert.Equal(AcaFamilyCap2025, ws.GetRemainingOopMax(NetworkTier.InNetwork));
        var snap = ws.GetSnapshot();
        Assert.DoesNotContain(snap, e => e.Type == AccumulatorType.AcaIndividualCap);
    }

    [Fact]
    public void Embedded_Mode_IgnoresAcaCapField_NoCapAccumulator()
    {
        // Embedded plans rely on the existing IndividualOutOfPocketMax to
        // bound members. Even if AcaIndividualCap is set on the config,
        // no AcaIndividualCap accumulator is seeded — that bucket is
        // Aggregate-mode-only.
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-emb",
            PlanName = "Embedded",
            PlanType = PlanType.PPO,
            FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
            IndividualOopMax = 5_000m,
            FamilyOopMax = 12_000m,
            AcaIndividualCap = AcaIndividualCap2025,
            IsAcaCapEnforced = true,
        };
        var ws = new AccumulatorWorkingSet(Array.Empty<AccumulatorSnapshot>(), plan);

        var snap = ws.GetSnapshot();
        Assert.DoesNotContain(snap, e => e.Type == AccumulatorType.AcaIndividualCap);
        // Embedded uses the existing IndividualOutOfPocketMax, not the cap.
        Assert.Equal(5_000m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }

    [Fact]
    public void Embedded_ZeroLimitPlaceholder_UsesAuthoredPlanOopLimit()
    {
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-placeholder",
            PlanName = "Placeholder Source",
            PlanType = PlanType.PPO,
            FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
            IndividualOopMax = 5_000m,
            FamilyOopMax = 10_000m,
        };
        var sourcePlaceholder = new AccumulatorSnapshot
        {
            Type = AccumulatorType.IndividualOutOfPocketMax,
            Scope = AccumulatorScope.Individual,
            NetworkTier = NetworkTier.InNetwork,
            LimitAmount = 0m,
            AccumulatedAmountAfter = 0m,
        };

        var ws = new AccumulatorWorkingSet([sourcePlaceholder], plan);

        Assert.Equal(5_000m, ws.GetRemainingOopMax(NetworkTier.InNetwork));
    }
}
