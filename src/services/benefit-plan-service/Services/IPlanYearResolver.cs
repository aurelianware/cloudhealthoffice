using BenefitPlanService.Models;

namespace BenefitPlanService.Services;

/// <summary>
/// Resolves the plan year used for ACA cap lookup (capability BP 5.7).
///
/// <para>
/// Precedence: <see cref="PlanYearDefinition.PlanYearStart"/>.Year (when
/// the plan author set up a 5.3 plan-year definition) → falls back to
/// <see cref="BenefitPlan.EffectiveDate"/>.Year. Plan-year definition is
/// authoritative because it carries the author's explicit choice; the
/// effective-date fallback is the conventional inference for plans that
/// predate 5.3 or skipped the optional definition.
/// </para>
/// </summary>
public interface IPlanYearResolver
{
    int Resolve(BenefitPlan plan);
}

public sealed class PlanYearResolver : IPlanYearResolver
{
    public int Resolve(BenefitPlan plan)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));

        if (plan.PlanYearDefinition is { PlanYearStart: var start } && start != default)
        {
            return start.Year;
        }

        return plan.EffectiveDate.Year;
    }
}
