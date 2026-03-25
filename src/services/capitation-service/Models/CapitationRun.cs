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
    /// Type of capitation run (Monthly, AdHocProvider, RetroAdjustment, WithholdRelease)
    /// </summary>
    [Required]
    public CapitationRunType RunType { get; set; } = CapitationRunType.Monthly;

    /// <summary>
    /// Line of business this run covers (denormalized from Criteria for display/filtering)
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

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
/// Criteria for a capitation run. LineOfBusiness is always required.
/// Additional fields are required or optional depending on RunType.
/// </summary>
public class CapitationRunCriteria
{
    /// <summary>
    /// Line of business for this capitation run (REQUIRED for all run types).
    /// Capitation runs are always scoped to a single LOB because rate structures,
    /// provider networks, and approval workflows differ by LOB.
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Specific provider NPI for ad-hoc runs (REQUIRED for AdHocProvider type,
    /// ignored for other run types). When set, the run generates statements for
    /// only this provider's active contract(s) in the specified LOB.
    /// </summary>
    [StringLength(10, MinimumLength = 10)]
    public string? ProviderNPI { get; set; }

    /// <summary>
    /// Optional: filter by contract type (e.g., run Professional cap separately
    /// from Global cap when they have different approval workflows).
    /// When null, includes all contract types in the LOB.
    /// </summary>
    public ContractType? ContractType { get; set; }

    /// <summary>
    /// Optional: limit the run to a specific set of provider NPIs. When non-empty,
    /// only contracts whose ProviderNPI appears in this list are processed. Useful
    /// for batch ad-hoc runs covering multiple providers without a full monthly run.
    /// </summary>
    public List<string> ProviderNPIs { get; set; } = new();

    /// <summary>
    /// Optional: limit the run to contracts that cover at least one of these plan IDs.
    /// When non-empty, contracts whose PlanIds list has no overlap with this list are
    /// excluded. Useful for running capitation for a specific product/plan.
    /// </summary>
    public List<string> PlanIds { get; set; } = new();

    /// <summary>
    /// Original capitation period being adjusted. Required for RetroAdjustment runs
    /// to identify which prior period to reprocess. Must be null for other run types.
    /// </summary>
    public DateTime? OriginalPeriod { get; set; }
}

/// <summary>
/// Type of capitation run — determines processing behavior and criteria requirements.
/// </summary>
public enum CapitationRunType
{
    /// <summary>
    /// Standard monthly capitation run. Generates statements for all active capitated
    /// providers in the specified LOB for the capitation period. This is the primary
    /// production run type (~90% of all runs).
    /// </summary>
    Monthly = 1,

    /// <summary>
    /// Ad-hoc run for a specific provider. Used when a provider was missed in the
    /// monthly run, a new contract was activated mid-month, or a correction is needed.
    /// Requires ProviderNPI to be set.
    /// </summary>
    AdHocProvider = 2,

    /// <summary>
    /// Retroactive adjustment run for a prior period. Reprocesses a previous capitation
    /// period to account for retroactive enrollment/disenrollment changes (e.g., retro
    /// 834 transactions), risk score updates, or rate corrections. Generates adjustment
    /// statements showing the delta from the original run.
    /// </summary>
    RetroAdjustment = 3,

    /// <summary>
    /// Quality withhold release run. Releases previously withheld funds to providers
    /// who met quality/performance metrics (HEDIS, STARS, value-based care targets).
    /// Typically run quarterly or annually. Generates withhold release adjustment
    /// statements, not new capitation statements.
    /// </summary>
    WithholdRelease = 4
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
    /// Type of capitation run
    /// </summary>
    [Required]
    public CapitationRunType RunType { get; set; } = CapitationRunType.Monthly;

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
