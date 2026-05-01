using System.ComponentModel.DataAnnotations;
using CloudHealthOffice.NcciEngine.Domain;

namespace CloudHealthOffice.NcciEngine.Models;

// ═══════════════════════════════════════════════════════════════════
// NCCI ENGINE — REQUEST / RESPONSE MODELS
//
// These types are the public contract consumed by adjudication
// workflows. Capability 5.7 wires this engine into claims-service via
// NcciEditsStage at Order=400 in the adjudication pipeline; future
// callers (state-Medicaid EDI, authorization-service pre-checks) bring
// their own mapper onto the same request shape.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// A single service line on a claim submitted for NCCI/MUE evaluation.
/// </summary>
public class ClaimServiceLine
{
    /// <summary>
    /// 1-based sequence number within the claim.
    /// </summary>
    [Required]
    public int LineNumber { get; set; }

    /// <summary>
    /// CPT or HCPCS procedure code.
    /// </summary>
    [Required]
    [StringLength(5, MinimumLength = 5)]
    public string ProcedureCode { get; set; } = string.Empty;

    /// <summary>
    /// Up to 4 procedure modifiers (e.g., "59", "XE", "26", "TC").
    /// Used to evaluate whether a -59/X{EPSU} modifier overrides bundling.
    /// </summary>
    public List<string> Modifiers { get; set; } = new();

    /// <summary>
    /// Units of service billed for this line.
    /// </summary>
    [Range(0.01, 9999)]
    public decimal Units { get; set; }

    /// <summary>
    /// Date of service (YYYYMMDD or ISO-8601).
    /// </summary>
    [Required]
    public DateOnly ServiceDate { get; set; }

    /// <summary>
    /// Place of service code (CMS POS). Used to select the applicable
    /// MUE column (professional vs. facility).
    /// </summary>
    [StringLength(2)]
    public string? PlaceOfServiceCode { get; set; }
}

/// <summary>
/// A claim submitted for NCCI and MUE evaluation.
/// </summary>
public class NcciScrubRequest
{
    /// <summary>
    /// Tenant that owns this claim.
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Claim identifier (for audit trail).
    /// </summary>
    [Required]
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Claim type: 837P (professional), 837I (institutional), 837D (dental).
    /// </summary>
    [Required]
    public string ClaimType { get; set; } = "837P";

    /// <summary>
    /// All service lines on the claim.
    /// </summary>
    [Required]
    [MinLength(1)]
    public List<ClaimServiceLine> ServiceLines { get; set; } = new();

    /// <summary>
    /// Date of service to use when resolving which NCCI/MUE quarter applies.
    /// Typically the earliest service date on the claim.
    /// </summary>
    public DateOnly? EffectiveDate { get; set; }
}

/// <summary>
/// The outcome of applying NCCI and MUE edits to one claim.
/// </summary>
public class NcciScrubResult
{
    /// <summary>
    /// Claim identifier echoed from the request.
    /// </summary>
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// True if the claim passed all NCCI and MUE edits.
    /// </summary>
    public bool Passed => EditFailures.Count == 0;

    /// <summary>
    /// All NCCI and MUE edit failures found on this claim.
    /// </summary>
    public List<NcciEditFailure> EditFailures { get; set; } = new();

    /// <summary>
    /// Total number of NCCI pair checks performed.
    /// </summary>
    public int NcciPairsChecked { get; set; }

    /// <summary>
    /// Total number of MUE checks performed.
    /// </summary>
    public int MueChecked { get; set; }
}

/// <summary>
/// A single NCCI or MUE edit failure on a claim.
/// </summary>
public class NcciEditFailure
{
    /// <summary>
    /// Edit type: NCCI_PAIR or MUE.
    /// </summary>
    public NcciEditType EditType { get; set; }

    /// <summary>
    /// Rule identifier for routing / work queue display.
    ///   NE001 = NCCI Column 1/Column 2 bundling edit
    ///   NE002 = MUE max units exceeded
    /// </summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the failure.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Column 1 procedure code (NCCI edits only).
    /// </summary>
    public string? Column1Code { get; set; }

    /// <summary>
    /// Column 2 procedure code that triggered the bundling edit.
    /// </summary>
    public string? Column2Code { get; set; }

    /// <summary>
    /// The line number(s) affected by this failure.
    /// </summary>
    public List<int> AffectedLineNumbers { get; set; } = new();

    /// <summary>
    /// Whether a -59/X{EPSU} modifier was present that could allow override
    /// (only relevant when ModifierIndicator = 1).
    /// </summary>
    public bool ModifierOverridePresent { get; set; }

    /// <summary>
    /// For MUE failures: units billed vs the MUE limit.
    /// </summary>
    public decimal? UnitsBilled { get; set; }

    /// <summary>
    /// For MUE failures: the MUE maximum units limit.
    /// </summary>
    public int? MueMaxUnits { get; set; }

    /// <summary>
    /// CARC (Claim Adjustment Reason Code) to apply on the EOB/835.
    ///   97  = Contractually required; payment included in allowance for another service/procedure.
    ///   B15 = This procedure code is not payable when billed with an additional procedure code.
    ///   B20 = Procedure code billed is not separated by procedure code billed by same provider.
    ///   151 = Payment adjusted because the payer deems the information submitted does not support
    ///         this many/frequency of services.
    /// </summary>
    public string? SuggestedCarc { get; set; }

    /// <summary>
    /// RARC (Remittance Advice Remark Code) to accompany the CARC.
    /// </summary>
    public string? SuggestedRarc { get; set; }
}

/// <summary>
/// Discriminates between NCCI Column 1/2 bundling edits and MUE unit limit edits.
/// </summary>
public enum NcciEditType
{
    /// <summary>
    /// NCCI Column 1 / Column 2 unbundling edit.
    /// Rule NE001.
    /// </summary>
    NcciPair,

    /// <summary>
    /// MUE maximum units exceeded.
    /// Rule NE002.
    /// </summary>
    Mue,
}

/// <summary>
/// Metadata about the NCCI/MUE table version currently loaded.
/// Returned by INcciEditService.GetTableVersionAsync() for audit/display.
/// </summary>
public class NcciTableVersion
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// CMS quarter label (e.g., "2025Q1", "2025Q3").
    /// </summary>
    public string Quarter { get; set; } = string.Empty;

    /// <summary>
    /// Date the files for this quarter were imported.
    /// </summary>
    public DateTime ImportedAt { get; set; }

    /// <summary>
    /// Number of NCCI edit pairs loaded for this quarter.
    /// </summary>
    public int NcciPairCount { get; set; }

    /// <summary>
    /// Number of MUE entries loaded for this quarter.
    /// </summary>
    public int MueEntryCount { get; set; }

    /// <summary>
    /// Effective date for the quarter (first day of the quarter).
    /// </summary>
    public DateTime EffectiveDate { get; set; }
}
