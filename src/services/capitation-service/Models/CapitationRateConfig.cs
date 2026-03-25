using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CapitationService.Models;

/// <summary>
/// Capitation-specific rate configuration for a provider contract.
/// Child record of ProviderContract — references parent by ContractId.
///
/// QNXT analog: Capitation module > Rate Cells / Auth Template configuration
/// that references the parent Contract record.
///
/// One ProviderContract may have multiple CapitationRateConfigs if the
/// contract is renegotiated mid-term (prior config retained for historical runs).
/// </summary>
public class CapitationRateConfig
{
    // ── Identity ──────────────────────────────────────────────────────────

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
    /// User-facing rate config number. Format: CAP-{NPI}-{Year}-{Seq}
    /// </summary>
    [Required]
    [StringLength(50)]
    public string RateConfigNumber { get; set; } = string.Empty;

    // ── Parent Contract Reference ─────────────────────────────────────────

    /// <summary>
    /// FK to ProviderContract.Id — required, cannot be null
    /// </summary>
    [Required]
    public string ContractId { get; set; } = string.Empty;

    // ── Denormalized Fields (see Consistency Policy in spec header) ────────
    // Set at creation from parent ProviderContract. Sync via
    // PUT /api/v1/contracts/{id}/sync-children when parent changes.

    /// <summary>
    /// Denormalized contract number from parent ProviderContract
    /// </summary>
    [StringLength(50)]
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized provider NPI from parent ProviderContract
    /// </summary>
    [StringLength(10)]
    public string ProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized provider name from parent ProviderContract
    /// </summary>
    [StringLength(300)]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized line of business from parent ProviderContract
    /// </summary>
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Null = never synced (created before sync feature). Non-null = last sync time.
    /// </summary>
    public DateTime? LastDenormSyncAt { get; set; }

    // ── Legacy Fields (retained for backward compatibility during migration) ──

    /// <summary>
    /// Provider type — retained on rate config during migration period.
    /// Canonical value lives on parent ProviderContract.
    /// </summary>
    public ProviderType ProviderType { get; set; }

    /// <summary>
    /// Benefit plan IDs — retained on rate config during migration period.
    /// Canonical value lives on parent ProviderContract.
    /// </summary>
    public List<string> PlanIds { get; set; } = new();

    // ── Capitation Scope ──────────────────────────────────────────────────

    /// <summary>
    /// Type of capitation arrangement (scope of services covered)
    /// </summary>
    [Required]
    public ContractType ContractType { get; set; }

    // ── Rate Tiers ────────────────────────────────────────────────────────

    /// <summary>
    /// PMPM rate tiers — age/sex/service category breakdowns
    /// </summary>
    public List<CapitationRateTier> RateTiers { get; set; } = new();

    // ── Risk Adjustment ───────────────────────────────────────────────────

    /// <summary>
    /// Whether rates are risk-adjusted (HCC/RAF scores applied to base PMPM)
    /// </summary>
    public bool RiskAdjusted { get; set; }

    /// <summary>
    /// Default risk score applied when member-level scores are unavailable (1.0 = average)
    /// </summary>
    public decimal DefaultRiskScore { get; set; } = 1.0m;

    // ── Financial Terms ───────────────────────────────────────────────────

    /// <summary>
    /// Quality withhold percentage (e.g. 0.10 = 10% held back pending quality metrics)
    /// </summary>
    public decimal WithholdPercentage { get; set; }

    /// <summary>
    /// Incentive pool — bonus beyond base capitation
    /// </summary>
    public decimal? IncentivePoolPercentage { get; set; }

    /// <summary>
    /// Per-member annual stop-loss threshold
    /// </summary>
    public decimal? StopLossThreshold { get; set; }

    /// <summary>
    /// Aggregate annual stop-loss for the entire rate config period
    /// </summary>
    public decimal? AggregateStopLoss { get; set; }

    // ── Effective Dating (can differ from contract dates for renegotiations)

    /// <summary>
    /// Rate config effective date
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Rate config termination date (null = open-ended)
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Current rate config status
    /// </summary>
    [Required]
    public CapitationRateConfigStatus Status { get; set; }
        = CapitationRateConfigStatus.Draft;

    // ── Audit ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Record creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// Capitation rate config lifecycle status
/// </summary>
public enum CapitationRateConfigStatus
{
    /// <summary>
    /// Rate config is being drafted, not yet effective
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Rate config is active and used for capitation calculations
    /// </summary>
    Active = 2,

    /// <summary>
    /// Rate config temporarily suspended
    /// </summary>
    Suspended = 3,

    /// <summary>
    /// A newer rate config is active for this contract
    /// </summary>
    Superseded = 6,

    /// <summary>
    /// Rate config terminated
    /// </summary>
    Terminated = 4,

    /// <summary>
    /// Rate config expired (past termination date)
    /// </summary>
    Expired = 5
}

// ContractType, CapitationRateTier, AgeSexCategory, LineOfBusiness, ProviderType
// — keep existing definitions in CapitationContract.cs for now.
// They will be cleaned up in Prompt 0c.
