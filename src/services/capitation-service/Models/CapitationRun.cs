using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CapitationService.Models;

/// <summary>
/// Represents a batch capitation run that generates payment statements for capitated providers.
/// The capitation equivalent of BillingRun — where BillingRun generates PremiumInvoices
/// (bills TO sponsors), CapitationRun generates CapitationStatements (payments TO providers).
/// </summary>
public class CapitationRun
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
    /// User-facing run number (e.g. "CAPRUN-2026-03-a1b2")
    /// Format: CAPRUN-{yyyy-MM}-{4-char-guid}
    /// </summary>
    [Required]
    [StringLength(50)]
    public string RunNumber { get; set; } = string.Empty;

    /// <summary>
    /// Description of the capitation run
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Capitation period (first of month, e.g. 2026-03-01)
    /// </summary>
    [Required]
    public DateTime CapitationPeriod { get; set; }

    /// <summary>
    /// Current status of the capitation run
    /// </summary>
    [Required]
    public CapitationRunStatus Status { get; set; } = CapitationRunStatus.Pending;

    /// <summary>
    /// Filter criteria for this capitation run
    /// </summary>
    public CapitationRunCriteria Criteria { get; set; } = new();

    /// <summary>
    /// Generated statement IDs
    /// </summary>
    public List<string> StatementIds { get; set; } = new();

    /// <summary>
    /// Total number of statements generated
    /// </summary>
    public int TotalStatements { get; set; }

    /// <summary>
    /// Total member-months across all statements
    /// </summary>
    public int TotalMemberMonths { get; set; }

    /// <summary>
    /// Total gross capitation across all statements (before withholds)
    /// </summary>
    public decimal TotalGrossCapitation { get; set; }

    /// <summary>
    /// Total withhold amount across all statements
    /// </summary>
    public decimal TotalWithholds { get; set; }

    /// <summary>
    /// Total adjustment amount across all statements
    /// </summary>
    public decimal TotalAdjustments { get; set; }

    /// <summary>
    /// Total net payable across all statements
    /// </summary>
    public decimal TotalNetPayable { get; set; }

    /// <summary>
    /// Number of distinct providers with statements in this run
    /// </summary>
    public int TotalProviders { get; set; }

    /// <summary>
    /// Capitation run created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created the capitation run
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
/// Filter criteria for a capitation run
/// </summary>
public class CapitationRunCriteria
{
    /// <summary>
    /// Specific provider NPIs to include (empty = all active capitated providers)
    /// </summary>
    public List<string> ProviderNPIs { get; set; } = new();

    /// <summary>
    /// Filter by line of business
    /// </summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// Filter by contract type
    /// </summary>
    public ContractType? ContractType { get; set; }

    /// <summary>
    /// Filter by specific plan IDs
    /// </summary>
    public List<string> PlanIds { get; set; } = new();
}

/// <summary>
/// Capitation run lifecycle status
/// </summary>
public enum CapitationRunStatus
{
    /// <summary>
    /// Run created, not yet started
    /// </summary>
    Pending,

    /// <summary>
    /// Run is actively generating statements
    /// </summary>
    Running,

    /// <summary>
    /// Run completed successfully
    /// </summary>
    Completed,

    /// <summary>
    /// Run failed during execution
    /// </summary>
    Failed,

    /// <summary>
    /// Run cancelled before or during execution
    /// </summary>
    Cancelled
}

/// <summary>
/// Request DTO for creating a capitation run
/// </summary>
public class CreateCapitationRunRequest
{
    /// <summary>
    /// Capitation period (first of month)
    /// </summary>
    [Required]
    public DateTime CapitationPeriod { get; set; }

    /// <summary>
    /// Filter criteria for the run
    /// </summary>
    public CapitationRunCriteria Criteria { get; set; } = new();

    /// <summary>
    /// Who is creating the run
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Optional description
    /// </summary>
    public string? Description { get; set; }
}
