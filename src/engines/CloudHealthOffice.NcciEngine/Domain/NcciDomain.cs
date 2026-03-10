namespace CloudHealthOffice.NcciEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// NCCI DOMAIN TYPES
//
// Models for CMS National Correct Coding Initiative (NCCI) edits:
//
//   NcciEditPair    — Column 1 / Column 2 procedure code pairs.
//                     When both codes appear on the same claim on the
//                     same date of service, Column 2 is normally bundled
//                     into Column 1 and must not be billed separately.
//                     Exception: a modifier can override bundling for
//                     procedures with ModifierIndicator = 1.
//
//   MueEntry        — Medically Unlikely Edits. CMS-defined maximum
//                     units of service per procedure code per day per
//                     beneficiary. Claims exceeding the MUE trigger a
//                     line-level denial.
//
// QNXT equivalents:
//   NcciEditPair    → NCCI_EDITS table (CMS quarterly file import)
//   MueEntry        → MUE table (CMS quarterly file import)
//
// Quarterly CMS updates:
//   Each quarter CMS publishes updated NCCI and MUE files.
//   Both document types carry an EffectiveDate / TerminationDate so
//   that historical adjudication retains the rules that were active
//   on the date of service.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// NCCI Column 1 / Column 2 edit pair (bundling edit).
/// </summary>
public class NcciEditPair
{
    /// <summary>
    /// Unique document id (Cosmos / Mongo).
    /// Stable key: "{TenantId}_{Col1}_{Col2}_{EffectiveDate:yyyyMMdd}"
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Multi-tenant partition key.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Column 1 procedure code (comprehensive code — the one that gets paid).
    /// </summary>
    public string Column1Code { get; set; } = string.Empty;

    /// <summary>
    /// Column 2 procedure code (component code — typically denied/bundled).
    /// </summary>
    public string Column2Code { get; set; } = string.Empty;

    /// <summary>
    /// Modifier indicator.
    ///   0 = Modifier not allowed — bundling cannot be overridden.
    ///   1 = Modifier allowed — a -59 / XE / XS / XP / XU or anatomic modifier
    ///       on the Column 2 code can override bundling.
    ///   9 = Edit does not apply (informational/retired pair).
    /// </summary>
    public NcciModifierIndicator ModifierIndicator { get; set; }

    /// <summary>
    /// CMS NCCI policy type that generated this pair.
    /// </summary>
    public NcciPolicyType PolicyType { get; set; }

    /// <summary>
    /// Quarter this pair became effective (YYYYMMDD of quarter start).
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Quarter this pair was terminated, or null if still active.
    /// </summary>
    public DateTime? TerminationDate { get; set; }
}

/// <summary>
/// MUE (Medically Unlikely Edit) entry.
/// Defines the maximum units of service for a procedure code per
/// beneficiary per day. Exceeding the MUE is a denial trigger.
/// </summary>
public class MueEntry
{
    /// <summary>
    /// Unique document id. Stable key: "{TenantId}_{ProcedureCode}_{EffectiveDate:yyyyMMdd}"
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Multi-tenant partition key.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// CPT or HCPCS procedure code.
    /// </summary>
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Maximum units of service allowed per beneficiary per date of service.
    /// </summary>
    public int MaxUnits { get; set; }

    /// <summary>
    /// MUE Adjudication Indicator (MAI).
    ///   1 = Claim line edit — each line adjudicated independently.
    ///   2 = Date of service edit — sum units across all claim lines.
    ///   3 = Date of service edit, absolute (anatomically impossible to exceed).
    /// </summary>
    public MueAdjudicationIndicator AdjudicationIndicator { get; set; }

    /// <summary>
    /// Whether this MUE applies to the professional setting (837P).
    /// </summary>
    public bool AppliesToProfessional { get; set; } = true;

    /// <summary>
    /// Whether this MUE applies to the outpatient facility setting (837I, outpatient).
    /// </summary>
    public bool AppliesToOutpatientFacility { get; set; } = true;

    /// <summary>
    /// Date this MUE entry became effective.
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Date this MUE entry was retired, or null if still active.
    /// </summary>
    public DateTime? TerminationDate { get; set; }
}

/// <summary>
/// NCCI Modifier Indicator values (CMS spec).
/// </summary>
public enum NcciModifierIndicator
{
    /// <summary>
    /// Modifier not allowed — bundling is absolute; no modifier can override.
    /// </summary>
    NotAllowed = 0,

    /// <summary>
    /// Modifier allowed — a -59 or derivative (XE/XS/XP/XU) on the Column 2
    /// code signals a distinct procedural service and may be paid separately.
    /// </summary>
    Allowed = 1,

    /// <summary>
    /// Edit does not apply (pair retired or informational row).
    /// </summary>
    NotApplicable = 9,
}

/// <summary>
/// NCCI policy category that generated the bundling edit.
/// </summary>
public enum NcciPolicyType
{
    /// <summary>
    /// Mutually exclusive codes — procedures that are clinically impossible
    /// to perform together on the same date.
    /// </summary>
    MutuallyExclusive,

    /// <summary>
    /// Column 2 is a component of Column 1 (unbundling).
    /// </summary>
    ProcedureToProc,

    /// <summary>
    /// Modifier-related bundling.
    /// </summary>
    ModifierRelated,
}

/// <summary>
/// MUE Adjudication Indicator — controls how units are counted for MUE comparison.
/// </summary>
public enum MueAdjudicationIndicator
{
    /// <summary>
    /// MAI 1 — each individual claim line is compared against the MUE in isolation.
    /// A claim with two lines for the same code can each carry up to MaxUnits.
    /// </summary>
    ClaimLine = 1,

    /// <summary>
    /// MAI 2 — all units for the procedure code on the same date of service are
    /// summed across all lines before comparing against MaxUnits.
    /// </summary>
    DateOfService = 2,

    /// <summary>
    /// MAI 3 — anatomically absolute. Units are summed across lines (same as MAI 2)
    /// but the limit reflects a hard biological ceiling that can never be exceeded.
    /// </summary>
    DateOfServiceAbsolute = 3,
}
