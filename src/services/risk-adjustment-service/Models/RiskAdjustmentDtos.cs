using System.ComponentModel.DataAnnotations;

namespace RiskAdjustmentService.Models;

/// <summary>
/// Response containing the risk score for a specific member and measurement year.
/// </summary>
public class MemberScoreResponse
{
    public string MemberId { get; set; } = string.Empty;
    public string? MemberFirstName { get; set; }
    public string? MemberLastName { get; set; }
    public int MeasurementYear { get; set; }
    public string RiskModel { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; }
    public decimal RiskScore { get; set; }
    public decimal DemographicFactor { get; set; }
    public decimal HccFactor { get; set; }
    public decimal InteractionFactor { get; set; }
    public int HccCategoryCount { get; set; }
    public int DiagnosisCount { get; set; }
    public ScoreStatus Status { get; set; }
    public DateTime CalculatedDate { get; set; }
    public bool IsSubmitted { get; set; }
}

/// <summary>
/// Request to calculate or recalculate a member's risk score.
/// Provide demographics (AgeAsOfPaymentYear, Gender) and DiagnosisCodes to
/// invoke the HCC scoring engine. SubscriberId defaults to MemberId when omitted.
/// </summary>
public class ScoreCalculationRequest
{
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Subscriber/group ID. Defaults to MemberId when not provided.</summary>
    [StringLength(50)]
    public string? SubscriberId { get; set; }

    [Required]
    public int MeasurementYear { get; set; }

    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    [StringLength(50)]
    public string RiskModel { get; set; } = "CMS-HCC";

    [StringLength(20)]
    public string ModelVersion { get; set; } = "V28";

    /// <summary>Member age as of the payment/rating year (used for demographic factor).</summary>
    [Range(0, 125)]
    public int? AgeAsOfPaymentYear { get; set; }

    /// <summary>Member gender: "M" or "F" (used for demographic factor).</summary>
    [StringLength(1)]
    public string? Gender { get; set; }

    /// <summary>
    /// ICD-10-CM diagnosis codes collected during the measurement year.
    /// When provided, the HCC scoring engine is invoked to compute the full RAF score.
    /// </summary>
    public List<string> DiagnosisCodes { get; set; } = new();

    /// <summary>Optional member first name (denormalized for display).</summary>
    [StringLength(100)]
    public string? MemberFirstName { get; set; }

    /// <summary>Optional member last name (denormalized for display).</summary>
    [StringLength(100)]
    public string? MemberLastName { get; set; }
}

/// <summary>
/// Measurement year data summary — aggregated statistics for a given year.
/// </summary>
public class MeasurementYearSummary
{
    public int MeasurementYear { get; set; }
    public string RiskModel { get; set; } = string.Empty;
    public LineOfBusiness? LineOfBusiness { get; set; }
    public int TotalMembers { get; set; }
    public int ScoredMembers { get; set; }
    public int SubmittedMembers { get; set; }
    public decimal AverageRiskScore { get; set; }
    public decimal MinRiskScore { get; set; }
    public decimal MaxRiskScore { get; set; }
    public decimal MedianRiskScore { get; set; }
    public int TotalHccCategories { get; set; }
    public int TotalDiagnoses { get; set; }
    public List<HccDistribution> TopHccCategories { get; set; } = new();
    public List<ScoreDistributionBucket> ScoreDistribution { get; set; } = new();
}

/// <summary>
/// HCC category frequency distribution entry.
/// </summary>
public class HccDistribution
{
    public string CategoryCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MemberCount { get; set; }
    public decimal AverageCoefficient { get; set; }
}

/// <summary>
/// Score distribution bucket for histogram display.
/// </summary>
public class ScoreDistributionBucket
{
    public decimal RangeFrom { get; set; }
    public decimal RangeTo { get; set; }
    public int MemberCount { get; set; }
}

/// <summary>
/// Batch score status update request.
/// </summary>
public class BatchStatusUpdate
{
    [Required]
    public List<string> MemberIds { get; set; } = new();

    [Required]
    public int MeasurementYear { get; set; }

    [Required]
    public ScoreStatus Status { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

/// <summary>
/// Risk score trend for a member across multiple years.
/// </summary>
public class MemberScoreTrend
{
    public string MemberId { get; set; } = string.Empty;
    public string? MemberFirstName { get; set; }
    public string? MemberLastName { get; set; }
    public List<YearlyScore> YearlyScores { get; set; } = new();
}

/// <summary>
/// A single year's risk score in a trend series.
/// </summary>
public class YearlyScore
{
    public int MeasurementYear { get; set; }
    public decimal RiskScore { get; set; }
    public decimal DemographicFactor { get; set; }
    public decimal HccFactor { get; set; }
    public int HccCategoryCount { get; set; }
    public ScoreStatus Status { get; set; }
}
