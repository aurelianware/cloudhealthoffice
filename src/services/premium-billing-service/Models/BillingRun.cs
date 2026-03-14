using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PremiumBillingService.Models;

/// <summary>
/// Represents a batch billing run that generates premium invoices for sponsor groups.
/// Modeled after PaymentRun pattern from payment-service.
/// </summary>
public class BillingRun
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-facing billing run number (e.g. "BR-2026-03-001")
    /// </summary>
    [Required]
    [StringLength(50)]
    public string BillingRunNumber { get; set; } = string.Empty;

    /// <summary>
    /// Description of the billing run
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Billing period (first of month, e.g. 2026-03-01)
    /// </summary>
    [Required]
    public DateTime BillingPeriod { get; set; }

    /// <summary>
    /// Current status of the billing run
    /// </summary>
    [Required]
    public BillingRunStatus Status { get; set; } = BillingRunStatus.Pending;

    /// <summary>
    /// Filter criteria for this billing run
    /// </summary>
    public BillingRunCriteria Criteria { get; set; } = new();

    /// <summary>
    /// Generated invoice IDs
    /// </summary>
    public List<string> InvoiceIds { get; set; } = new();

    /// <summary>
    /// Total number of invoices generated
    /// </summary>
    public int TotalInvoices { get; set; }

    /// <summary>
    /// Total premium amount across all invoices
    /// </summary>
    public decimal TotalPremiumAmount { get; set; }

    /// <summary>
    /// Total adjustment amount across all invoices
    /// </summary>
    public decimal TotalAdjustmentAmount { get; set; }

    /// <summary>
    /// Total member count across all invoices
    /// </summary>
    public int TotalMembers { get; set; }

    /// <summary>
    /// Billing run created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created the billing run
    /// </summary>
    [StringLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Execution started timestamp
    /// </summary>
    public DateTime? ExecutionStartedAt { get; set; }

    /// <summary>
    /// Execution completed timestamp
    /// </summary>
    public DateTime? ExecutionCompletedAt { get; set; }

    /// <summary>
    /// Execution duration in seconds
    /// </summary>
    public double? ExecutionDurationSeconds { get; set; }

    /// <summary>
    /// Error messages during execution
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Warning messages during execution
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Filter criteria for a billing run
/// </summary>
public class BillingRunCriteria
{
    /// <summary>
    /// Specific group numbers to bill (empty = all active sponsors)
    /// </summary>
    public List<string> GroupNumbers { get; set; } = new();

    /// <summary>
    /// Filter by line of business
    /// </summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// Filter by billing frequency
    /// </summary>
    public BillingFrequency? BillingFrequency { get; set; }
}

public enum BillingRunStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}

public enum BillingFrequency
{
    Monthly = 1,
    Quarterly = 3,
    SemiAnnually = 6,
    Annual = 12
}

/// <summary>
/// Request DTO for creating a billing run
/// </summary>
public class CreateBillingRunRequest
{
    [Required]
    public DateTime BillingPeriod { get; set; }

    public BillingRunCriteria Criteria { get; set; } = new();

    public string? CreatedBy { get; set; }

    public string? Description { get; set; }
}
