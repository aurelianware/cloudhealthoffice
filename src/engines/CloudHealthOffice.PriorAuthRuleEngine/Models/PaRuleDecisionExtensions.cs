using CloudHealthOffice.PriorAuthRuleEngine.Domain;

namespace CloudHealthOffice.PriorAuthRuleEngine.Models;

/// <summary>
/// Extension helpers for interpreting <see cref="PaRuleDecision"/> outcomes.
/// Shared by FhirService.CrdService and BenefitPlanService.AdjudicationController
/// to ensure rule-decision interpretation stays in sync.
/// </summary>
public static class PaRuleDecisionExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> when the decision indicates prior authorization
    /// is required: a Deny outcome, or a meaningful Pend (i.e. a rule actually fired,
    /// as opposed to a no-match / no-op / rule-error Pend).
    /// </summary>
    public static bool IsPriorAuthRequired(this PaRuleDecision? decision)
    {
        if (decision is null)
            return false;

        if (decision.Outcome is PaDecisionOutcome.Deny)
            return true;

        if (decision.Outcome is not PaDecisionOutcome.Pend)
            return false;

        return decision.FiringRuleId is not ("NoRulesConfigured" or "NoRuleMatch" or "NoOp")
            && !decision.FiringRuleId.StartsWith("RuleError:", StringComparison.Ordinal);
    }
}
