using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RiskAdjustmentService.Models;

/// <summary>
/// Per-member risk adjustment score record.
/// Stores the HCC (Hierarchical Condition Category) risk score for a member
/// within a specific measurement year.
///
/// Risk adjustment scores drive capitation payments in Medicare Advantage,
/// Medicaid managed care, and ACA marketplace plans.
/// </summary>
public class MemberRiskScore
{
    /// <summary>
    /// Multi-tenant partition key.
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique document identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Member ID (links to Member Service).
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Member first name (denormalized for display).
    /// </summary>
    [StringLength(100)]
    public string? MemberFirstName { get; set; }

    /// <summary>
    /// Member last name (denormalized for display).
    /// </summary>
    [StringLength(100)]
    public string? MemberLastName { get; set; }

    /// <summary>
    /// Member date of birth (used for demographic risk factor calculation).
    /// </summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>
    /// Member gender (M/F — used in demographic risk factor calculation).
    /// </summary>
    [StringLength(1)]
    public string? Gender { get; set; }

    /// <summary>
    /// Measurement year (e.g., 2026).
    /// Risk scores are calculated per calendar/plan year.
    /// </summary>
    [Required]
    public int MeasurementYear { get; set; }

    /// <summary>
    /// Risk adjustment model used for scoring.
    /// CMS-HCC (Medicare), HHS-HCC (ACA), CDPS (Medicaid), etc.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string RiskModel { get; set; } = "CMS-HCC";

    /// <summary>
    /// Model version (e.g., V28, V24, V05).
    /// </summary>
    [Required]
    [StringLength(20)]
    public string ModelVersion { get; set; } = "V28";

    /// <summary>
    /// Line of Business this score applies to.
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Final composite risk score (RAF — Risk Adjustment Factor).
    /// Sum of demographic factor + all HCC coefficients + disease interaction terms.
    /// A score of 1.0 represents average expected cost.
    /// </summary>
    [Required]
    public decimal RiskScore { get; set; }

    /// <summary>
    /// Demographic base factor (age/gender component of RAF).
    /// </summary>
    public decimal DemographicFactor { get; set; }

    /// <summary>
    /// Sum of all HCC-based coefficients (disease burden component).
    /// </summary>
    public decimal HccFactor { get; set; }

    /// <summary>
    /// Sum of disease interaction terms (comorbidity adjustments).
    /// </summary>
    public decimal InteractionFactor { get; set; }

    /// <summary>
    /// Individual HCC categories that contribute to this score.
    /// </summary>
    public List<HccCategory> HccCategories { get; set; } = new();

    /// <summary>
    /// Diagnosis codes (ICD-10) that mapped to the HCC categories.
    /// </summary>
    public List<RiskDiagnosis> Diagnoses { get; set; } = new();

    /// <summary>
    /// Disease interaction terms applied to this member.
    /// </summary>
    public List<DiseaseInteraction> Interactions { get; set; } = new();

    /// <summary>
    /// Whether this score has been submitted for payment (e.g., RAPS/EDPS submission).
    /// </summary>
    public bool IsSubmitted { get; set; }

    /// <summary>
    /// Date the score was submitted to CMS/payer.
    /// </summary>
    public DateTime? SubmittedDate { get; set; }

    /// <summary>
    /// Score calculation status.
    /// </summary>
    [Required]
    public ScoreStatus Status { get; set; } = ScoreStatus.Calculated;

    /// <summary>
    /// Date this score was calculated.
    /// </summary>
    public DateTime CalculatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date this record was created.
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Date this record was last updated.
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Created by.
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Audit: Last updated by.
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// An HCC category that contributes to a member's risk score.
/// </summary>
public class HccCategory
{
    /// <summary>
    /// HCC category number (e.g., HCC19 = Diabetes without Complication).
    /// </summary>
    [Required]
    [StringLength(20)]
    public string CategoryCode { get; set; } = string.Empty;

    /// <summary>
    /// Category description.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Coefficient (weight) this category adds to the risk score.
    /// </summary>
    [Required]
    public decimal Coefficient { get; set; }

    /// <summary>
    /// ICD-10 codes that mapped to this category.
    /// </summary>
    public List<string> SourceDiagnosisCodes { get; set; } = new();

    /// <summary>
    /// Whether this category was superseded by a higher-severity category
    /// in the HCC hierarchy (e.g., HCC19 is trumped by HCC17).
    /// </summary>
    public bool IsSuperseded { get; set; }

    /// <summary>
    /// The category that superseded this one (if applicable).
    /// </summary>
    [StringLength(20)]
    public string? SupersededBy { get; set; }
}

/// <summary>
/// A diagnosis code that contributes to risk adjustment scoring.
/// </summary>
public class RiskDiagnosis
{
    /// <summary>
    /// ICD-10 diagnosis code.
    /// </summary>
    [Required]
    [StringLength(10)]
    public string DiagnosisCode { get; set; } = string.Empty;

    /// <summary>
    /// Diagnosis description.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// HCC category this diagnosis maps to.
    /// </summary>
    [StringLength(20)]
    public string? MappedHccCategory { get; set; }

    /// <summary>
    /// Source of the diagnosis (encounter, claim, chart review, etc.).
    /// </summary>
    [StringLength(50)]
    public string? Source { get; set; }

    /// <summary>
    /// Date of service when the diagnosis was recorded.
    /// </summary>
    public DateTime? ServiceDate { get; set; }

    /// <summary>
    /// Provider NPI who recorded the diagnosis.
    /// </summary>
    [StringLength(10)]
    public string? ProviderNPI { get; set; }

    /// <summary>
    /// Encounter or claim ID that is the source of this diagnosis.
    /// </summary>
    [StringLength(50)]
    public string? SourceEncounterId { get; set; }
}

/// <summary>
/// Disease interaction term applied to a member's risk score.
/// CMS-HCC models include interaction terms for certain comorbidity combinations.
/// </summary>
public class DiseaseInteraction
{
    /// <summary>
    /// Interaction term label (e.g., "HCC47_gCancer", "DIABETES_CHF").
    /// </summary>
    [Required]
    [StringLength(100)]
    public string InteractionLabel { get; set; } = string.Empty;

    /// <summary>
    /// HCC categories involved in this interaction.
    /// </summary>
    public List<string> InvolvedCategories { get; set; } = new();

    /// <summary>
    /// Coefficient (weight) this interaction adds to the risk score.
    /// </summary>
    public decimal Coefficient { get; set; }
}

/// <summary>
/// Score calculation status.
/// </summary>
public enum ScoreStatus
{
    /// <summary>Score has been calculated.</summary>
    Calculated = 1,

    /// <summary>Score is under review (e.g., chart review pending).</summary>
    UnderReview = 2,

    /// <summary>Score has been validated and finalized.</summary>
    Finalized = 3,

    /// <summary>Score has been submitted to CMS/payer.</summary>
    Submitted = 4,

    /// <summary>Score has been accepted by CMS/payer.</summary>
    Accepted = 5,

    /// <summary>Score was rejected and needs correction.</summary>
    Rejected = 6
}

/// <summary>
/// Line of Business (consistent with other services).
/// </summary>
public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}
