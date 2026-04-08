using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace CloudHealthOffice.PriorAuthRuleEngine.Abstractions;

// ─────────────────────────────────────────────────────────────────
// IPaRule — implemented by every rule class
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// A single prior authorization decision rule.
///
/// Rules are stateless — all inputs come through PaRuleContext.
/// The engine calls EvaluateAsync() in priority order and stops
/// at the first non-null result.
///
/// Return null to signal "no match — continue to the next rule."
/// Return a PaRuleDecision with Approve or Deny to short-circuit.
///
/// Rules must never throw — catch exceptions internally and return
/// null (fall through) or log and re-throw if PendOnRuleError is false.
/// </summary>
public interface IPaRule
{
    /// <summary>Stable identifier matching PaRuleDocument.RuleType.</summary>
    string RuleType { get; }

    RuleCategory Category   { get; }
    int Priority            { get; }

    /// <summary>
    /// Evaluate the rule against the provided context.
    /// Returns null when the rule does not apply — engine continues.
    /// Returns a PaRuleDecision to short-circuit further evaluation.
    /// </summary>
    Task<PaRuleDecision?> EvaluateAsync(
        PaRuleDocument config,
        PaRuleContext context,
        CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────
// IPriorAuthRuleEngine — the engine itself
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Evaluates all applicable PA rules for a request and returns the
/// first decisive outcome (Approve/Deny) or Pend when no rule matches.
///
/// Consumed by:
///   PasAutoAdjudicator  — Rule 5 (replaces default PEND)
///   authorization-service — direct 278 submission path
/// </summary>
public interface IPriorAuthRuleEngine
{
    /// <summary>
    /// Resolve applicable rules and evaluate them in order.
    /// Returns the first Approve or Deny, or Pend when exhausted.
    /// </summary>
    Task<PaRuleDecision> EvaluateAsync(
        PaRuleContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Return the ordered list of rules that would be evaluated for a
    /// given rule set key. Used by the portal "why was this pended?" view
    /// and for clinical staff auditing.
    /// </summary>
    Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(
        RuleSetKey key,
        CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────
// Pre-fetch services — resolved before rule evaluation starts
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Fetches a provider's PA approval history over a lookback window.
/// Used by RegulatoryExemption rules (TX gold card).
/// Host implementation calls authorization-service or its Cosmos store directly.
/// </summary>
public interface IProviderApprovalHistoryService
{
    Task<ProviderApprovalHistory?> GetAsync(
        string npi,
        int lookbackDays,
        CancellationToken ct = default);
}

/// <summary>
/// Fetches a member's authorised quantity/visit totals for the current benefit period.
/// Used by QuantityLimit rules.
/// Host implementation queries the authorization-service's approved PA store.
/// </summary>
public interface IMemberAuthHistoryService
{
    Task<MemberAuthHistory?> GetAsync(
        string memberId,
        IReadOnlyList<string> procedureCodes,
        string benefitPeriod,
        CancellationToken ct = default);
}
