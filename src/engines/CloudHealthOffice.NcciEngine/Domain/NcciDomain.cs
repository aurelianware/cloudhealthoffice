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
    /// For CMS-loaded rows this is the file's Deletion Date, which is
    /// exclusive: the edit is not in effect on that date.
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Which CMS PTP table the pair came from (<see cref="NcciSettings"/>).
    /// Null means the row applies to every setting (built-in seed rows and
    /// rows imported before settings were tracked).
    /// </summary>
    public string? Setting { get; set; }

    /// <summary>CMS "PTP Edit Rationale" text, when loaded from a CMS file.</summary>
    public string? Rationale { get; set; }

    /// <summary>CMS "*=in existence prior to 1996" flag.</summary>
    public bool ExistedPrior1996 { get; set; }

    /// <summary>Quarter label of the CMS release that last wrote this row (e.g. "2026Q4").</summary>
    public string? SourceQuarter { get; set; }

    /// <summary>
    /// "{Setting}:{Part}" of the CMS file that last wrote this row. A later
    /// load of the same file slot reconciles rows it no longer lists.
    /// </summary>
    public string? SourceKey { get; set; }

    /// <summary>Id of the load that last wrote this row.</summary>
    public string? LoadId { get; set; }
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

    /// <summary>
    /// Which CMS MUE table the entry came from (<see cref="NcciSettings"/>).
    /// Null means the row applies to every setting (built-in seed rows).
    /// </summary>
    public string? Setting { get; set; }

    /// <summary>CMS "MUE Rationale" text, when loaded from a CMS file.</summary>
    public string? Rationale { get; set; }

    /// <summary>Quarter label of the CMS release that last wrote this row (e.g. "2026Q4").</summary>
    public string? SourceQuarter { get; set; }
}

/// <summary>
/// CMS publishes separate PTP and MUE tables per setting. The values are
/// stored as strings (not an enum) so the persisted shape is the same
/// under every serializer the repositories use.
/// </summary>
public static class NcciSettings
{
    /// <summary>Practitioner tables; applied to professional (837P) claims.</summary>
    public const string Practitioner = "PRACTITIONER";

    /// <summary>Outpatient hospital tables; applied to institutional (837I) claims.</summary>
    public const string OutpatientHospital = "OUTPATIENT_HOSPITAL";

    /// <summary>
    /// Marks a claim type CMS publishes no NCCI table for (e.g. 837D). It
    /// matches no CMS-loaded row, only setting-less (seed / legacy) rows.
    /// </summary>
    public const string None = "NONE";

    /// <summary>The setting whose tables apply to a claim type ("837P" / "837I"); <see cref="None"/> otherwise.</summary>
    public static string ForClaimType(string? claimType) => claimType?.Trim().ToUpperInvariant() switch
    {
        "837P" or "PROFESSIONAL" => Practitioner,
        "837I" or "INSTITUTIONAL" => OutpatientHospital,
        _ => None,
    };

    /// <summary>Parse a setting name; accepts the constants and common aliases.</summary>
    public static bool TryParse(string? value, out string setting)
    {
        setting = value?.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "PRACTITIONER" or "PRA" or "PROFESSIONAL" => Practitioner,
            "OUTPATIENT_HOSPITAL" or "OPH" or "HOSPITAL" or "OUTPATIENT" => OutpatientHospital,
            _ => string.Empty,
        };
        return setting.Length > 0;
    }
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
