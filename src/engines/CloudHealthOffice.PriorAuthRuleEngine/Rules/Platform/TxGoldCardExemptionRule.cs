using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Rules;

namespace CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;

/// <summary>
/// Texas Gold Card Law — HB 3459 (2021), effective 09/01/2022.
///
/// A provider who has maintained a 90%+ approval rate over the lookback
/// period (minimum decision threshold must also be met) is exempt from
/// prior authorization requirements for the services covered by the rule.
///
/// Statutory reference: Texas Insurance Code §4201.653
/// Medicaid managed care is excluded by §4201.652, including STAR families.
///
/// RuleType:  "TxGoldCardExemption"
/// Category:  RegulatoryExemption (band 0 — runs before all clinical rules)
/// Priority:  1 (first rule evaluated in the engine)
///
/// Config fields used:
///   GoldCardApprovalRateThreshold  — decimal (default 0.90)
///   GoldCardMinimumDecisions       — int     (default 20)
///   ProcedureCodes / Prefixes      — scope to specific procedure types if needed
/// </summary>
public sealed class TxGoldCardExemptionRule : PaRuleBase
{
    public override string RuleType     => "TxGoldCardExemption";
    public override RuleCategory Category => RuleCategory.RegulatoryExemption;
    public override int Priority        => 1;

    public override Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default)
    {
        // Enforce applicability in executable code as well as seed configuration:
        // previously persisted/tenant-enabled Medicaid rules must also be inert.
        // Returning null continues ordinary review; it is not an adverse decision.
        if (context.Lob == PaLineOfBusiness.Medicaid || config.Lob == PaLineOfBusiness.Medicaid)
            return Task.FromResult<PaRuleDecision?>(null);

        // ── Scope check ───────────────────────────────────────────
        if (!AppliesToProcedures(config, context.ProcedureCodes))
            return Task.FromResult<PaRuleDecision?>(null);

        // ── Requires pre-fetched provider history ─────────────────
        if (context.ProviderHistory is null)
            return Task.FromResult<PaRuleDecision?>(null);

        var history = context.ProviderHistory;

        var minDecisions      = config.GoldCardMinimumDecisions      ?? 20;
        var approvalThreshold = config.GoldCardApprovalRateThreshold ?? 0.90m;

        // Insufficient history — cannot confirm gold card status
        if (history.TotalDecisions < minDecisions)
            return Task.FromResult<PaRuleDecision?>(null);

        // Below threshold — not exempt
        if (history.ApprovalRate < approvalThreshold)
            return Task.FromResult<PaRuleDecision?>(null);

        // Gold card confirmed — approve without clinical review
        return Task.FromResult<PaRuleDecision?>(Approve(config));
    }
}
