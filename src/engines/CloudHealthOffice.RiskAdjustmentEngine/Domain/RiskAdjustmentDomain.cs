namespace CloudHealthOffice.RiskAdjustmentEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// ENUMS
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// HCC risk model variant.
///
/// CMS-HCC v28: Used by Medicare Advantage plans (MA-PD) starting 2024.
///   Replaces v24; adds conditions, refines hierarchies, includes 115 HCCs.
///
/// HHS-HCC: Used by ACA Marketplace (individual/small group) plans.
///   Separate ICD-10 crosswalk and factor table.
/// </summary>
public enum HccModel
{
    CmsHccV28,  // Medicare Advantage (default)
    HhsHcc      // ACA Marketplace
}

/// <summary>
/// Member's enrollment segment — determines which factor table to apply.
/// CMS publishes separate factors for community vs. institutional members,
/// and for dual-eligible vs. non-dual.
/// </summary>
public enum EnrollmentSegment
{
    CommunityNonDual,
    CommunityFullDual,
    CommunityPartialDual,
    Institutional,
    NewEnrollee
}

/// <summary>
/// Member's gender for demographic factor lookup.
/// </summary>
public enum MemberGender
{
    Male,
    Female
}

// ═══════════════════════════════════════════════════════════════════
// INPUT
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// All information needed to compute a risk score for one member.
/// Diagnosis codes are collected from encounter submissions across the
/// measurement year (T-1) before the payment year begins.
/// </summary>
public record RiskScoreInput
{
    public string MemberId { get; init; } = default!;
    public string SubscriberId { get; init; } = default!;

    public HccModel Model { get; init; } = HccModel.CmsHccV28;
    public EnrollmentSegment Segment { get; init; } = EnrollmentSegment.CommunityNonDual;

    // ── Demographics ─────────────────────────────────────────────────────
    public int AgeAsOfPaymentYear { get; init; }
    public MemberGender Gender { get; init; }

    /// <summary>
    /// Originally Disabled: member entitled to Medicare due to disability before age 65.
    /// Adds an interaction factor in the CMS-HCC model.
    /// </summary>
    public bool OriginallyDisabled { get; init; }

    // ── Diagnosis codes ───────────────────────────────────────────────────
    /// <summary>
    /// Unique ICD-10-CM codes collected from all accepted encounters during
    /// the measurement year. Duplicates are pre-deduplicated by the caller.
    /// </summary>
    public List<string> DiagnosisCodes { get; init; } = [];
}

// ═══════════════════════════════════════════════════════════════════
// INTERMEDIATE
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// A single Hierarchical Condition Category — one node in the HCC taxonomy.
/// </summary>
public record HccCategory
{
    public int CategoryCode { get; init; }
    public string Description { get; init; } = default!;

    /// <summary>
    /// Relative factor for this HCC in the CommunityNonDual segment.
    /// Factors are model- and segment-specific; this field stores the
    /// community/non-dual value used by most MA members.
    /// </summary>
    public decimal RelativeFactor { get; init; }
}

/// <summary>
/// Maps a set of ICD-10 codes to an HCC category.
/// </summary>
public record HccMapping
{
    public string Icd10Code { get; init; } = default!;
    public int HccCategoryCode { get; init; }
    public HccModel Model { get; init; }
}

/// <summary>
/// A hierarchy rule: when the <see cref="DominantCategory"/> is present,
/// all <see cref="SubordinateCategories"/> are removed from the member's
/// risk profile. This prevents double-counting conditions of different severity
/// within the same disease group.
/// </summary>
public record HccHierarchyRule
{
    public int DominantCategory { get; init; }
    public IReadOnlyList<int> SubordinateCategories { get; init; } = [];
    public HccModel Model { get; init; }
}

/// <summary>
/// Demographic factor for one age/sex/segment cell.
/// </summary>
public record DemographicFactor
{
    public int AgeFrom { get; init; }
    public int AgeTo { get; init; }       // inclusive
    public MemberGender Gender { get; init; }
    public EnrollmentSegment Segment { get; init; }
    public HccModel Model { get; init; }
    public decimal Factor { get; init; }
}

// ═══════════════════════════════════════════════════════════════════
// OUTPUT
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Complete risk score result for one member.
/// </summary>
public record RiskScoreResult
{
    public string MemberId { get; init; } = default!;
    public HccModel Model { get; init; }
    public EnrollmentSegment Segment { get; init; }

    // ── Score breakdown ───────────────────────────────────────────────────

    /// <summary>
    /// Demographic (age/sex) base factor.
    /// </summary>
    public decimal DemographicFactor { get; init; }

    /// <summary>
    /// Each HCC that contributed to the score, after hierarchy resolution.
    /// </summary>
    public List<HccContribution> HccContributions { get; init; } = [];

    /// <summary>
    /// Sum of all HCC relative factors (before normalization).
    /// </summary>
    public decimal TotalHccFactor { get; init; }

    /// <summary>
    /// Final risk score = DemographicFactor + TotalHccFactor.
    /// Used by CMS to compute the plan's monthly capitation payment.
    /// </summary>
    public decimal FinalRiskScore { get; init; }

    // ── Audit trail ───────────────────────────────────────────────────────

    /// <summary>
    /// Map from each input ICD-10 code to the HCC it was mapped to (null if unmapped).
    /// </summary>
    public Dictionary<string, int?> DiagnosisToHccMap { get; init; } = [];

    /// <summary>
    /// HCCs that were present but removed by hierarchy rules.
    /// </summary>
    public List<int> SuppressedHccs { get; init; } = [];
}

/// <summary>
/// One HCC's contribution to the final risk score.
/// </summary>
public record HccContribution
{
    public int CategoryCode { get; init; }
    public string Description { get; init; } = default!;
    public decimal RelativeFactor { get; init; }

    /// <summary>ICD-10 codes that triggered this HCC.</summary>
    public List<string> SourceDiagnosisCodes { get; init; } = [];
}
