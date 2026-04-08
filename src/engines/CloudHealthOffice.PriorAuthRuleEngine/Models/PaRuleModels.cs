using CloudHealthOffice.PriorAuthRuleEngine.Domain;

namespace CloudHealthOffice.PriorAuthRuleEngine.Models;

// ─────────────────────────────────────────────────────────────────
// Rule set key — identifies which rules apply to a PA request
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Composite key that identifies a rule set.
///
/// Resolution hierarchy (most specific wins):
///   1. TenantId + StateCode + Lob + Program   (tenant STAR+ override)
///   2. null    + StateCode + Lob + Program     (platform STAR+ rules)
///   3. TenantId + StateCode + Lob + null       (tenant TX Medicaid rules)
///   4. null    + StateCode + Lob + null         (platform TX Medicaid rules)
///   5. No match                                → Pend
///
/// Program handles TX-specific program distinctions (STAR, STARPlus, STARKids)
/// within the Medicaid LOB. For non-TX states, Program is typically null.
/// </summary>
public record RuleSetKey
{
    public required string StateCode        { get; init; }  // "TX", "CA", "FL"
    public required PaLineOfBusiness Lob    { get; init; }  // Medicaid, Exchange, ...
    public string? Program                  { get; init; }  // "STAR", "STARPlus", "STARKids", null
    public string? TenantId                 { get; init; }  // null = platform rule

    public override string ToString() =>
        $"{TenantId ?? "platform"}/{StateCode}/{Lob}/{Program ?? "any"}";
}

// ─────────────────────────────────────────────────────────────────
// Evaluation context — everything a rule needs to decide
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// All inputs available to rule implementations during evaluation.
/// Pre-resolved before the engine runs to avoid repeated async calls
/// inside individual rules.
/// </summary>
public record PaRuleContext
{
    // ── Identity ──────────────────────────────────────────────────
    public required string TenantId                             { get; init; }
    public required string StateCode                            { get; init; }
    public required PaLineOfBusiness Lob                        { get; init; }

    /// <summary>
    /// State program code — critical for TX.
    /// "STAR" | "STARPlus" | "STARKids" | null (non-program states)
    /// </summary>
    public string? Program                                      { get; init; }

    // ── The request ───────────────────────────────────────────────
    public required string RequestingProviderNpi                { get; init; }
    public required string ServicingProviderNpi                 { get; init; }
    public string? ServicingProviderTaxonomy                    { get; init; }
    public required string MemberId                             { get; init; }
    public required DateOnly ServiceDate                        { get; init; }
    public required IReadOnlyList<string> ProcedureCodes        { get; init; }
    public required IReadOnlyList<string> DiagnosisCodes        { get; init; }
    public string? PlaceOfServiceCode                           { get; init; }
    public required decimal EstimatedCost                       { get; init; }
    public int RequestedUnits                                   { get; init; } = 1;

    // ── Pre-resolved context ──────────────────────────────────────
    // Populated by PriorAuthRuleEngine before calling rules, so
    // each rule implementation does not need its own async fetch.

    /// <summary>
    /// Provider approval history over the lookback window.
    /// Required for gold card exemption rules.
    /// Null when provider history service is unavailable or not configured.
    /// </summary>
    public ProviderApprovalHistory? ProviderHistory             { get; init; }

    /// <summary>
    /// Quantity of the requested service already authorised in the current benefit period.
    /// Required for QuantityLimit rules.
    /// Null when member history service is unavailable or not configured.
    /// </summary>
    public MemberAuthHistory? MemberHistory                     { get; init; }

    /// <summary>Member date of birth — required for MemberAge rules (STARKids).</summary>
    public DateOnly? MemberDateOfBirth                          { get; init; }
}

/// <summary>Provider PA approval history over a rolling lookback window.</summary>
public record ProviderApprovalHistory
{
    public required string Npi              { get; init; }
    public required int LookbackDays        { get; init; }
    public required int TotalDecisions      { get; init; }
    public required int ApprovedDecisions   { get; init; }

    /// <summary>Approval rate as a decimal fraction (0.0 – 1.0).</summary>
    public decimal ApprovalRate => TotalDecisions > 0
        ? (decimal)ApprovedDecisions / TotalDecisions
        : 0m;
}

/// <summary>Member's PA history for a specific procedure group in the current benefit period.</summary>
public record MemberAuthHistory
{
    public required string MemberId                 { get; init; }
    public required string BenefitPeriod            { get; init; }  // "2026"
    public required IReadOnlyList<string> ProcedureCodes { get; init; }
    public required int AuthorisedUnits             { get; init; }
    public required int AuthorisedVisits            { get; init; }
    public required decimal AuthorisedAmount        { get; init; }
}

// ─────────────────────────────────────────────────────────────────
// Decision — what the engine returns
// ─────────────────────────────────────────────────────────────────

public record PaRuleDecision
{
    public required PaDecisionOutcome Outcome   { get; init; }
    public string? DenialCode                   { get; init; }  // X12 AAA03 / CARC
    public string? DenialReason                 { get; init; }
    public required string FiringRuleId         { get; init; }  // which rule produced this
    public required string FiringRuleName       { get; init; }
    public required string ResolvedRuleSetKey   { get; init; }  // for audit trail
    public IReadOnlyList<string> EvaluatedRules { get; init; } = []; // all rule IDs tried
    public long ElapsedMs                       { get; init; }
}

// ─────────────────────────────────────────────────────────────────
// Persisted rule document — stored in Cosmos / Mongo
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Persisted representation of a PA rule.
///
/// Rule logic is not stored as code in the document — the document
/// stores the rule's configuration parameters. Logic lives in the
/// corresponding IPaRule implementation class which reads these params.
///
/// RuleType maps the document to its C# implementation:
///   "TxGoldCardExemption"    → TxGoldCardExemptionRule
///   "ProcedureRequiresAuth"  → ProcedureRequiresAuthRule
///   "QuantityLimit"          → QuantityLimitRule
///   "DiagnosisRequired"      → DiagnosisRequiredRule
///   "PlaceOfServiceRequired" → PlaceOfServiceRequiredRule
///   "MemberAgeLimit"         → MemberAgeLimitRule
///   "ProviderTypeExemption"  → ProviderTypeExemptionRule
/// </summary>
public class PaRuleDocument
{
    /// <summary>Document ID: "{stateCode}:{lob}:{program ?? "any"}:{ruleId}"</summary>
    public string Id                            { get; set; } = string.Empty;

    public required string RuleId              { get; set; }
    public required string RuleName            { get; set; }
    public string? Description                 { get; set; }

    // ── Rule set membership ───────────────────────────────────────
    public required string StateCode           { get; set; }   // partition key
    public required PaLineOfBusiness Lob       { get; set; }
    public string? Program                     { get; set; }
    public string? TenantId                    { get; set; }   // null = platform

    // ── Classification ────────────────────────────────────────────
    public required RuleCategory Category      { get; set; }
    public required RuleScope Scope            { get; set; }
    public required int Priority               { get; set; }   // lower = evaluated first
    public bool IsEnabled                      { get; set; } = true;
    public required string RuleType            { get; set; }   // maps to IPaRule impl

    // ── Rule-specific configuration ───────────────────────────────
    // Populated differently per RuleType.
    // IPaRule implementations cast/read the fields they need.

    /// <summary>Procedure codes this rule applies to. Empty = all procedures.</summary>
    public IReadOnlyList<string> ProcedureCodes        { get; set; } = [];

    /// <summary>Procedure code prefixes for range matching (e.g. "K", "A", "E" for DME).</summary>
    public IReadOnlyList<string> ProcedureCodePrefixes { get; set; } = [];

    /// <summary>Required ICD-10 codes for DiagnosisRequired rules.</summary>
    public IReadOnlyList<string> RequiredDiagnosisCodes { get; set; } = [];

    /// <summary>Place of service codes that trigger this rule.</summary>
    public IReadOnlyList<string> PlaceOfServiceCodes   { get; set; } = [];

    /// <summary>Provider taxonomy prefixes exempt from PA (ProviderTypeExemption).</summary>
    public IReadOnlyList<string> ExemptTaxonomyPrefixes { get; set; } = [];

    /// <summary>Dollar threshold for cost-based auto-approve rules.</summary>
    public decimal? CostThreshold               { get; set; }

    /// <summary>Unit limit per benefit period for QuantityLimit rules.</summary>
    public int? UnitLimit                       { get; set; }

    /// <summary>Visit limit per benefit period for QuantityLimit rules.</summary>
    public int? VisitLimit                      { get; set; }

    /// <summary>Approval rate threshold for gold card exemption (0.0–1.0).</summary>
    public decimal? GoldCardApprovalRateThreshold { get; set; }

    /// <summary>Minimum decision count before gold card applies.</summary>
    public int? GoldCardMinimumDecisions        { get; set; }

    /// <summary>Maximum member age for MemberAge rules.</summary>
    public int? MaxMemberAgeYears               { get; set; }

    /// <summary>Minimum member age for MemberAge rules.</summary>
    public int? MinMemberAgeYears               { get; set; }

    /// <summary>Denial code to use when this rule denies (X12 AAA03).</summary>
    public string? DenialCode                   { get; set; }

    /// <summary>Human-readable denial reason template.</summary>
    public string? DenialReasonTemplate         { get; set; }

    public DateTime CreatedAt                   { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt                   { get; set; } = DateTime.UtcNow;
}

// ─────────────────────────────────────────────────────────────────
// Options
// ─────────────────────────────────────────────────────────────────

public class PriorAuthRuleEngineOptions
{
    public const string SectionName = "PriorAuthRuleEngine";

    /// <summary>Redis TTL for cached rule sets (default: 15 minutes).</summary>
    public TimeSpan RuleSetCacheTtl            { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Lookback window for gold card approval rate calculation (default: 180 days).
    /// TX HB 3229 specifies 12 months; 180 days is conservative.
    /// </summary>
    public int GoldCardLookbackDays            { get; set; } = 180;

    /// <summary>
    /// When true, rule evaluation errors produce Pend rather than re-throwing.
    /// Recommended for production — a rule engine failure should not block care.
    /// </summary>
    public bool PendOnRuleError                { get; set; } = true;
}
