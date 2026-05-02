using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentService.Models;

/// <summary>
/// Represents a payment run batch job
/// Groups approved claims for payment processing
/// </summary>
public class PaymentRun
{
    /// <summary>
    /// Multi-tenant partition key
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique payment run identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Payment run number (user-facing)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PaymentRunNumber { get; set; } = string.Empty;

    /// <summary>
    /// Payment run description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Payment run status
    /// </summary>
    [Required]
    public PaymentRunStatus Status { get; set; } = PaymentRunStatus.Pending;

    /// <summary>
    /// Filter criteria used for this payment run
    /// </summary>
    public PaymentRunCriteria Criteria { get; set; } = new();

    /// <summary>
    /// Generated payments in this run
    /// </summary>
    public List<string> PaymentIds { get; set; } = new();

    /// <summary>
    /// Generated <c>EraEnvelopeRecord</c> ids — one per trading partner
    /// in this run (5.10 batched 835 generation). Empty for runs that
    /// pre-date 5.10 or whose claims didn't resolve to any trading
    /// partner.
    /// </summary>
    public List<string> EraEnvelopeIds { get; set; } = new();

    /// <summary>
    /// Claims included in this payment run
    /// </summary>
    public List<string> ClaimIds { get; set; } = new();

    /// <summary>
    /// Total number of claims processed
    /// </summary>
    public int TotalClaims { get; set; }

    /// <summary>
    /// Total payment amount
    /// </summary>
    public decimal TotalPaymentAmount { get; set; }

    /// <summary>
    /// Check number range assigned
    /// </summary>
    public string? CheckNumberStart { get; set; }

    /// <summary>
    /// Check number range end
    /// </summary>
    public string? CheckNumberEnd { get; set; }

    /// <summary>
    /// Next check number to assign
    /// </summary>
    public int NextCheckNumber { get; set; }

    /// <summary>
    /// Payment run created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created the payment run
    /// </summary>
    [StringLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Payment run execution started
    /// </summary>
    public DateTime? ExecutionStartedAt { get; set; }

    /// <summary>
    /// Payment run execution completed
    /// </summary>
    public DateTime? ExecutionCompletedAt { get; set; }

    /// <summary>
    /// Execution duration in seconds
    /// </summary>
    public double? ExecutionDurationSeconds { get; set; }

    /// <summary>
    /// Error messages if any
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Warnings during execution
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Payment method for this run (ACH, Check)
    /// </summary>
    [StringLength(10)]
    public string PaymentMethod { get; set; } = "ACH";

    /// <summary>
    /// Payment date
    /// </summary>
    public DateTime PaymentDate { get; set; } = DateTime.UtcNow.AddDays(3);

    /// <summary>
    /// Scheduled run (vs manual)
    /// </summary>
    public bool IsScheduled { get; set; } = false;

    /// <summary>
    /// Cron expression if scheduled
    /// </summary>
    public string? ScheduleExpression { get; set; }
}

/// <summary>
/// Payment run filter criteria
/// </summary>
public class PaymentRunCriteria
{
    /// <summary>
    /// Line of Business filter
    /// </summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// Provider NPI filter (pay-to provider)
    /// </summary>
    [StringLength(10)]
    public string? ProviderNPI { get; set; }

    /// <summary>
    /// Service date from
    /// </summary>
    public DateTime? ServiceDateFrom { get; set; }

    /// <summary>
    /// Service date to
    /// </summary>
    public DateTime? ServiceDateTo { get; set; }

    /// <summary>
    /// Claim submission date from
    /// </summary>
    public DateTime? SubmissionDateFrom { get; set; }

    /// <summary>
    /// Claim submission date to
    /// </summary>
    public DateTime? SubmissionDateTo { get; set; }

    /// <summary>
    /// Minimum claim amount
    /// </summary>
    public decimal? MinClaimAmount { get; set; }

    /// <summary>
    /// Maximum claim amount
    /// </summary>
    public decimal? MaxClaimAmount { get; set; }

    /// <summary>
    /// Claim IDs to include (manual selection)
    /// </summary>
    public List<string> IncludeClaimIds { get; set; } = new();

    /// <summary>
    /// Claim IDs to exclude
    /// </summary>
    public List<string> ExcludeClaimIds { get; set; } = new();

    /// <summary>
    /// Only include claims with specific member IDs
    /// </summary>
    public List<string> MemberIds { get; set; } = new();

    /// <summary>
    /// Group payments by provider
    /// </summary>
    public bool GroupByProvider { get; set; } = true;

    /// <summary>
    /// Maximum claims per payment
    /// </summary>
    public int? MaxClaimsPerPayment { get; set; }
}

/// <summary>
/// Claim status enumeration (mirrored from claims-service)
/// </summary>
public enum ClaimStatus
{
    Draft,
    Submitted,
    Acknowledged,
    InReview,
    Approved,
    Denied,
    PartiallyApproved,
    Paid,
    Appealed,
    Finalized
}

/// <summary>
/// Line of Business enumeration
/// </summary>
public enum LineOfBusiness
{
    Commercial,
    Medicare,
    Medicaid,
    Marketplace
}

public enum PaymentRunStatus
{
    Pending,      // Created but not executed
    Running,      // Currently executing
    Completed,    // Successfully completed
    Failed,       // Failed with errors
    Cancelled     // Manually cancelled
}
