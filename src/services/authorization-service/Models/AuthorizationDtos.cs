using System.ComponentModel.DataAnnotations;
using AuthorizationService.Services.Rfai;

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

    /// <summary>
    /// What documentation the reviewer needs, when the status is being moved to
    /// Pended with review decision A4. Naming it here is what turns a pended
    /// status into an actual additional-information request the provider can
    /// answer; a pend with nothing named asks the provider for nothing and
    /// raises no request.
    /// </summary>
    public List<RequestedInformationItem> RequestedInformation { get; set; } = new();

    /// <summary>When the requested documentation is expected.</summary>
    public DateTime? InformationDueDate { get; set; }
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

    /// <summary>
    /// The documentation an A4 (pended) decision is asking the provider for.
    /// Coded, per item, because a receiving system has to act on it — see
    /// <see cref="RequestedInformationItem"/>. <see cref="PendReason"/> and
    /// <see cref="FollowUpAction"/> supplement these; they do not replace them.
    ///
    /// An A4 decision that names nothing here raises no additional-information
    /// request: nothing was actually asked for.
    /// </summary>
    public List<RequestedInformationItem> RequestedInformation { get; set; } = new();

    /// <summary>When the requested documentation is expected back.</summary>
    public DateTime? InformationDueDate { get; set; }
}

/// <summary>
/// SLA status for an at-risk authorization (returned by the deadline watchdog endpoint)
/// </summary>
public class AuthorizationSlaStatus
{
    public string Id { get; set; } = string.Empty;
    public string AuthorizationNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public AuthorizationStatus Status { get; set; }
    public string? LevelOfService { get; set; }
    public DateTime SlaStartedAt { get; set; }
    public DateTime SlaDeadline { get; set; }
    public double HoursRemaining { get; set; }
    public double HoursElapsed { get; set; }
    public double PercentConsumed { get; set; }
    public SlaEscalationLevel EscalationLevel { get; set; }
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
