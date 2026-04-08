using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace CloudHealthOffice.PriorAuthRuleEngine.Rules;

/// <summary>
/// Abstract base for all IPaRule implementations.
/// Provides procedure code matching helpers and standardised decision factories.
/// Concrete rules override EvaluateAsync and use the protected helpers.
/// </summary>
public abstract class PaRuleBase : IPaRule
{
    public abstract string RuleType  { get; }
    public abstract RuleCategory Category { get; }
    public abstract int Priority     { get; }

    public abstract Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default);

    // ── Procedure code matching ───────────────────────────────────

    /// <summary>
    /// True when any requested procedure code is covered by this rule's config.
    /// Matches exact codes and prefix ranges (e.g. "K" covers K0001–K9999).
    /// An empty config.ProcedureCodes + empty Prefixes means "all procedures".
    /// </summary>
    protected static bool AppliesToProcedures(
        PaRuleDocument config, IReadOnlyList<string> requestedCodes)
    {
        if (config.ProcedureCodes.Count == 0 && config.ProcedureCodePrefixes.Count == 0)
            return true; // rule applies to all procedures

        foreach (var code in requestedCodes)
        {
            if (config.ProcedureCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
                return true;

            if (config.ProcedureCodePrefixes.Any(p =>
                    code.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when at least one required diagnosis code is present in the request.
    /// </summary>
    protected static bool HasRequiredDiagnosis(
        PaRuleDocument config, IReadOnlyList<string> requestedDx)
    {
        if (config.RequiredDiagnosisCodes.Count == 0) return true;

        return requestedDx.Any(dx =>
            config.RequiredDiagnosisCodes.Contains(dx, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the place of service is in the rule's configured POS list.
    /// An empty list means "any POS".
    /// </summary>
    protected static bool MatchesPlaceOfService(
        PaRuleDocument config, string? requestedPos)
    {
        if (config.PlaceOfServiceCodes.Count == 0) return true;
        if (string.IsNullOrEmpty(requestedPos)) return false;

        return config.PlaceOfServiceCodes.Contains(requestedPos, StringComparer.OrdinalIgnoreCase);
    }

    // ── Decision factories ────────────────────────────────────────

    protected static PaRuleDecision Approve(PaRuleDocument config) => new()
    {
        Outcome             = PaDecisionOutcome.Approve,
        FiringRuleId        = config.RuleId,
        FiringRuleName      = config.RuleName,
        ResolvedRuleSetKey  = BuildKey(config)
    };

    protected static PaRuleDecision Deny(PaRuleDocument config, string? reasonOverride = null) => new()
    {
        Outcome             = PaDecisionOutcome.Deny,
        DenialCode          = config.DenialCode ?? "AUTH-DENY",
        DenialReason        = reasonOverride ?? config.DenialReasonTemplate ?? config.RuleName,
        FiringRuleId        = config.RuleId,
        FiringRuleName      = config.RuleName,
        ResolvedRuleSetKey  = BuildKey(config)
    };

    private static string BuildKey(PaRuleDocument d) =>
        $"{d.TenantId ?? "platform"}/{d.StateCode}/{d.Lob}/{d.Program ?? "any"}";
}
