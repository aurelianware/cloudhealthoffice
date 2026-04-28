using BenefitPlanService.Models;

namespace BenefitPlanService.Services;

/// <summary>
/// Single source of truth for the "is the ACA per-member cap enforced
/// for this plan" decision (capability BP 5.7 G8 gated rollout).
/// Consumed by:
///
/// <list type="bullet">
///   <item><see cref="ChoBenefitPlanProvider"/> when projecting
///         <see cref="BenefitPlan"/> onto the engine's
///         <c>BenefitPlanConfig.IsAcaCapEnforced</c>.</item>
///   <item><see cref="FhirInsurancePlanProjector"/> when emitting the
///         <c>insuranceplan-aca-cap-enforced</c> CHO extension on the
///         FHIR projection (capability BP 5.8 Decision 13).</item>
/// </list>
///
/// <para>
/// Extracted as a static helper rather than an interface because the
/// decision is a pure function of (FamilyAccumulatorModel, PublishedAt)
/// and a fixed UTC cutoff. No per-tenant knobs, no I/O, no DI surface.
/// Two consumers + one cutoff = one helper.
/// </para>
///
/// <para>
/// See <c>docs/architecture/family-accumulator-models.md</c> for the
/// rollout posture and <c>docs/architecture/fhir-insuranceplan-projection.md</c>
/// for the projection-side use of <see cref="IsEnforced"/>.
/// </para>
/// </summary>
public static class AcaCapEnforcementPolicy
{
    /// <summary>
    /// Cutoff that distinguishes legacy plans (hydrate with
    /// <c>IsAcaCapEnforced = false</c>) from post-5.7 publishes (which set
    /// it true automatically). Plans with <see cref="BenefitPlan.PublishedAt"/>
    /// at or after this UTC instant get runtime ACA cap enforcement on
    /// Aggregate mode; legacy plans behave as they did pre-5.7 until an
    /// operator amends + republishes them.
    /// </summary>
    public static readonly DateTime CutoffUtc =
        new(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// True when <paramref name="plan"/> is Aggregate-mode AND has been
    /// (or will be) published at or after <see cref="CutoffUtc"/>.
    /// Drafts (<c>PublishedAt</c> null) are treated as post-cutoff because
    /// any subsequent publish will be after the cutoff.
    /// </summary>
    public static bool IsEnforced(BenefitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.FamilyAccumulatorModel != FamilyAccumulatorModel.Aggregate)
            return false;

        if (!plan.PublishedAt.HasValue) return true;

        var publishedAt = plan.PublishedAt.Value.Kind == DateTimeKind.Utc
            ? plan.PublishedAt.Value
            : plan.PublishedAt.Value.ToUniversalTime();

        return publishedAt >= CutoffUtc;
    }
}
