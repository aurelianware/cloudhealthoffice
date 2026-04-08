namespace CloudHealthOffice.PriorAuthRuleEngine.Domain;

// ─────────────────────────────────────────────────────────────────
// Enumerations
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// The outcome a rule produces. Rules return null to signal no match —
/// the engine then falls through to the next rule in priority order.
/// </summary>
public enum PaDecisionOutcome
{
    /// <summary>PA approved — auth number generated.</summary>
    Approve,

    /// <summary>PA denied — denial code and reason attached.</summary>
    Deny,

    /// <summary>
    /// PA pended — no rule reached a conclusion; routes to clinical review queue.
    /// This is the terminal outcome when the engine exhausts all rules with no match.
    /// </summary>
    Pend
}

/// <summary>
/// Organises rules into evaluation bands.
/// Bands run in numeric order; within a band, Priority determines order.
/// RegulatoryExemption (band 0) always runs before ClinicalCriteria (band 1).
/// </summary>
public enum RuleCategory
{
    /// <summary>
    /// State law exemptions evaluated before any clinical criteria.
    /// A passing exemption short-circuits all remaining rules.
    /// Examples: TX HB 3229 gold card law, CMS gold-card pilot.
    /// </summary>
    RegulatoryExemption = 0,

    /// <summary>
    /// Clinical criteria — does this procedure require PA for this LOB/program?
    /// The most common rule category.
    /// </summary>
    ClinicalCriteria = 1,

    /// <summary>
    /// Quantity / visit limits per benefit period.
    /// E.g. TX STAR: chiropractic visits > 20/year require PA.
    /// </summary>
    QuantityLimit = 2,

    /// <summary>
    /// Diagnosis codes required to support authorization of the procedure.
    /// E.g. certain DME only covered with specific ICD-10 codes.
    /// </summary>
    DiagnosisRequired = 3,

    /// <summary>
    /// Place of service restrictions.
    /// E.g. procedure code requires hospital setting (POS 21) to need PA;
    /// same code in office (POS 11) does not.
    /// </summary>
    PlaceOfService = 4,

    /// <summary>
    /// Age-specific program rules.
    /// E.g. STARKids: members under 21 follow EPSDT rules.
    /// </summary>
    MemberAge = 5,

    /// <summary>
    /// Provider taxonomy / type exemptions.
    /// E.g. certain procedure codes do not require PA when rendered by
    /// a primary care provider (taxonomy 207Q*).
    /// </summary>
    ProviderType = 6
}

/// <summary>
/// Whether the rule is a platform-level rule (ships with CHO) or a
/// tenant-specific override. Tenant rules take precedence over platform
/// rules within the same RuleSetKey + Priority band.
/// </summary>
public enum RuleScope
{
    /// <summary>Applies to all tenants running in this state/LOB/program.</summary>
    Platform,

    /// <summary>Applies to one specific tenant; overrides platform rules.</summary>
    Tenant
}

/// <summary>
/// Line of business — matches AuthorizationService.Models.LineOfBusiness exactly.
/// Numeric values must stay in sync.
/// </summary>
public enum PaLineOfBusiness
{
    Commercial = 1,
    Medicare   = 2,
    Medicaid   = 3,
    Exchange   = 4,
    TRICARE    = 5,
    VA         = 6
}
