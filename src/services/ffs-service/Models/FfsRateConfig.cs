using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FfsService.Models;

/// <summary>
/// Fee-for-service rate configuration stub for a provider contract.
/// Child record of ProviderContract — references parent by ContractId.
///
/// QNXT analog: Fee Schedule module > Schedule Assignment that references
/// the parent Contract record.
///
/// This is a placeholder for the full FFS rate engine. It establishes the
/// schema and parent-child relationship so hybrid contracts can reference
/// both capitation and FFS rate configs.
/// </summary>
public class FfsRateConfig
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
    /// User-facing config number. Format: FFS-{NPI}-{Year}-{Seq}
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

    // ── Denormalized Fields ───────────────────────────────────────────────
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

    // ── Fee Schedule Reference ────────────────────────────────────────────

    /// <summary>
    /// External fee schedule identifier (e.g., "CMS-PFS-2026", "CUSTOM-COMMERCIAL-A")
    /// </summary>
    [Required]
    [StringLength(100)]
    public string FeeScheduleId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable fee schedule name
    /// </summary>
    [StringLength(300)]
    public string FeeScheduleName { get; set; } = string.Empty;

    /// <summary>
    /// Percentage of fee schedule (e.g., 1.10 = 110% of Medicare)
    /// </summary>
    public decimal FeeSchedulePercentage { get; set; } = 1.0m;

    // ── Modifier Rules (stub) ─────────────────────────────────────────────

    /// <summary>
    /// Multiple procedure reduction applies
    /// </summary>
    public bool MultipleProcedureReduction { get; set; } = true;

    /// <summary>
    /// Bilateral procedure adjustment applies
    /// </summary>
    public bool BilateralAdjustment { get; set; } = true;

    // ── Effective Dating ──────────────────────────────────────────────────

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
    public FfsRateConfigStatus Status { get; set; }
        = FfsRateConfigStatus.Draft;

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
/// FFS rate config lifecycle status
/// </summary>
public enum FfsRateConfigStatus
{
    /// <summary>
    /// Rate config is being drafted, not yet effective
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Rate config is active
    /// </summary>
    Active = 2,

    /// <summary>
    /// A newer rate config is active for this contract
    /// </summary>
    Superseded = 3,

    /// <summary>
    /// Rate config terminated
    /// </summary>
    Terminated = 4,

    /// <summary>
    /// Rate config expired (past termination date)
    /// </summary>
    Expired = 5
}

/// <summary>
/// Line of Business (matches other CHO services)
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
