using System.ComponentModel.DataAnnotations;

namespace AuthorizationService.Models;

/// <summary>
/// Authorization validation response (for claims processing)
/// </summary>
public class AuthorizationValidationResponse
{
    public string AuthorizationNumber { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public AuthorizationStatus Status { get; set; }
    public DateTime? ApprovedServiceDateFrom { get; set; }
    public DateTime? ApprovedServiceDateTo { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public decimal? ApprovedUnits { get; set; }
    public string? ValidationMessage { get; set; }
}

/// <summary>
/// Authorization status update request
/// </summary>
public class AuthorizationStatusUpdate
{
    [Required]
    public AuthorizationStatus Status { get; set; }

    [StringLength(2)]
    public string? ReviewDecision { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }
}

/// <summary>
/// Authorization response (278 response transaction)
/// </summary>
public class AuthorizationResponse
{
    [Required]
    [StringLength(50)]
    public string ControlNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(2)]
    public string ReviewDecision { get; set; } = string.Empty; // A1/A2/A3/A4

    public decimal? ApprovedUnits { get; set; }
    public DateTime? ApprovedServiceDateFrom { get; set; }
    public DateTime? ApprovedServiceDateTo { get; set; }
    public DateTime? ExpirationDate { get; set; }

    [StringLength(10)]
    public string? DenialReasonCode { get; set; }

    [StringLength(500)]
    public string? DenialReason { get; set; }

    [StringLength(500)]
    public string? PendReason { get; set; }

    [StringLength(1000)]
    public string? FollowUpAction { get; set; }

    [StringLength(200)]
    public string? ReviewerName { get; set; }

    [StringLength(20)]
    public string? ReviewerPhone { get; set; }
}

/// <summary>
/// Authorizations summary statistics
/// </summary>
public class AuthorizationsSummary
{
    public int TotalAuthorizations { get; set; }
    public int ApprovedAuthorizations { get; set; }
    public int DeniedAuthorizations { get; set; }
    public int PendedAuthorizations { get; set; }
    public int ModifiedAuthorizations { get; set; }
    public int ExpiredAuthorizations { get; set; }
    public decimal AverageReviewDays { get; set; }
    public decimal AverageTurnaroundDays { get; set; }
    public decimal ApprovalRate { get; set; }
}
