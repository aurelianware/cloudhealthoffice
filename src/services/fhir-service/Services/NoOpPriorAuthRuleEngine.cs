using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace FhirService.Services;

/// <summary>
/// No-op implementation registered when Redis/DB are unavailable (local dev).
/// Always returns Pend so the adjudicator flow continues without crashing.
/// </summary>
internal sealed class NoOpPriorAuthRuleEngine : IPriorAuthRuleEngine
{
    public Task<PaRuleDecision> EvaluateAsync(
        PaRuleContext context, CancellationToken ct = default)
        => Task.FromResult(new PaRuleDecision
        {
            Outcome            = PaDecisionOutcome.Pend,
            FiringRuleId       = "NoOp",
            FiringRuleName     = "NoOp",
            ResolvedRuleSetKey = "none"
        });

    public Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PaRuleDocument>>([]);
}
