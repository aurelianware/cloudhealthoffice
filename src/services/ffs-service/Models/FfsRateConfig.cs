using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FfsService.Models;

/// <summary>
/// Fee-for-service rate configuration for a provider contract. Child record of
/// ProviderContract — references parent by ContractId.
///
/// QNXT analog: Fee Schedule module &gt; Schedule Assignment that references
/// the parent Contract record.
///
/// <para>
/// <b>Superseded by the contract's reimbursement terms (ADR 016 phase 2).</b>
/// This service never grew a controller or a repository, so nothing wrote these
/// documents and nothing priced from them. A fee-for-service rate is now a
/// reimbursement term on the contract itself — the fee schedule, the percentage
/// of it and the two modifier toggles below map one-to-one onto a
/// <c>FeeScheduleRef</c> term's schedule, multiplier and modifier rules — which
/// is where the pricing engine already reads it:
/// <c>POST /api/v1/contracts/{id}/reimbursement-terms/from-ffs-rate-config</c>
/// in provider-contracts-service takes exactly these fields.
/// </para>
///
/// <para>
/// The model stays for the phase 3 backfill, which reads any documents a tenant
/// has in the <c>FfsRateConfig</c> collection and turns them into terms. Do not
/// add an API over it.
/// </para>
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
