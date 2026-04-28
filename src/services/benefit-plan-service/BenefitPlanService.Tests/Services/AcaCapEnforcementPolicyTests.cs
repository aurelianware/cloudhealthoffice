using BenefitPlanService.Models;
using BenefitPlanService.Services;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.8 — verifies the extracted <see cref="AcaCapEnforcementPolicy"/>
/// preserves the BP 5.7 G8 gating semantics that previously lived inline
/// in <c>ChoBenefitPlanProvider.ResolveIsAcaCapEnforced</c>. Both the
/// engine-config projection and the FHIR InsurancePlan projection now
/// call <see cref="AcaCapEnforcementPolicy.IsEnforced"/>; this test
/// pins the rule so neither call site can drift.
/// </summary>
public sealed class AcaCapEnforcementPolicyTests
{
    [Fact]
    public void Embedded_plan_is_never_enforced()
    {
        var plan = new BenefitPlan
        {
            FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
            PublishedAt = DateTime.UtcNow,
            VersionState = PlanVersionState.Published,
        };

        AcaCapEnforcementPolicy.IsEnforced(plan).Should().BeFalse();
    }

    [Fact]
    public void Aggregate_draft_with_no_PublishedAt_is_treated_as_post_cutoff()
    {
        var plan = new BenefitPlan
        {
            FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate,
            PublishedAt = null,
            VersionState = PlanVersionState.Draft,
        };

        AcaCapEnforcementPolicy.IsEnforced(plan).Should().BeTrue(
            "drafts will be published after the cutoff by definition");
    }

    [Fact]
    public void Aggregate_plan_published_at_or_after_cutoff_is_enforced()
    {
        var plan = new BenefitPlan
        {
            FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate,
            PublishedAt = AcaCapEnforcementPolicy.CutoffUtc,
            VersionState = PlanVersionState.Published,
        };

        AcaCapEnforcementPolicy.IsEnforced(plan).Should().BeTrue();
    }

    [Fact]
    public void Aggregate_plan_published_after_cutoff_is_enforced()
    {
        var plan = new BenefitPlan
        {
            FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate,
            PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(7),
            VersionState = PlanVersionState.Published,
        };

        AcaCapEnforcementPolicy.IsEnforced(plan).Should().BeTrue();
    }

    [Fact]
    public void Aggregate_legacy_plan_published_before_cutoff_is_not_enforced()
    {
        var plan = new BenefitPlan
        {
            FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate,
            PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(-1),
            VersionState = PlanVersionState.Published,
        };

        AcaCapEnforcementPolicy.IsEnforced(plan).Should().BeFalse(
            "legacy Aggregate plans don't get a surprise mid-year cap");
    }

    [Fact]
    public void Local_kind_PublishedAt_is_normalized_to_utc()
    {
        // PublishedAt without a Kind annotation is conventionally UTC on
        // the wire but Local in some test scaffolding. The policy must
        // normalize either way so the comparison is stable.
        var localBefore = DateTime.SpecifyKind(
            AcaCapEnforcementPolicy.CutoffUtc.AddDays(-1).ToLocalTime(),
            DateTimeKind.Local);

        var plan = new BenefitPlan
        {
            FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate,
            PublishedAt = localBefore,
            VersionState = PlanVersionState.Published,
        };

        AcaCapEnforcementPolicy.IsEnforced(plan).Should().BeFalse();
    }

    [Fact]
    public void Cutoff_matches_BP_5_7_ratified_value()
    {
        // Pinned to 2026-04-28 00:00 UTC by the BP 5.7 G8 rollout. The
        // FHIR projector emits the aca-cap-enforced extension based on
        // this exact instant; changing it without coordinating with
        // active plan documents would surprise consumers mid-year.
        AcaCapEnforcementPolicy.CutoffUtc.Should().Be(
            new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Throws_on_null_plan()
    {
        Action act = () => AcaCapEnforcementPolicy.IsEnforced(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
