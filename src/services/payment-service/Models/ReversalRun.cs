using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaymentService.Models;

/// <summary>
/// Operator-initiated 835 reversal batch (capability 5.12b). Mirrors
/// <see cref="PaymentRun"/> structurally — the second instance of the
/// operator-initiated batch workflow pattern in payment-service. One
/// row per reversal cycle: criteria → list of <c>ClaimAdjustment</c>
/// rows in <c>PendingReversal</c> → batched 835 reversal envelopes
/// (CLP02="22", CAS amounts sign-flipped) → cross-service void
/// invocations against claims-service so each predecessor claim
/// transitions to Voided + the originating adjustment lifecycle
/// transitions PendingReversal → Active.
///
/// <para>
/// Pure data shape; lifecycle owned by <c>IReversalRunService</c>.
/// Phase 1 has no creation idempotency — operators click "Run
/// reversals" and a fresh run is persisted each time (pattern parity
/// with PaymentRun, which also has no Idempotency-Key support today).
/// Re-execution is guarded by <see cref="Status"/>: only
/// <see cref="ReversalRunStatus.Pending"/> runs can be executed; calling
/// <c>ExecuteReversalRunAsync</c> on a Running/Completed/Failed/Cancelled
/// run throws <see cref="InvalidOperationException"/>. Idempotency on
/// the cross-service void calls is enforced by claims-service
/// (<c>AlreadyVoided</c> outcome → 200 OK), so re-running a
/// successfully-completed reversal cycle against a fresh ReversalRun is
/// safe even though the run row itself isn't reused.
/// </para>
/// </summary>
public class ReversalRun
{
    /// <summary>Multi-tenant partition key (set by repository from request context).</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Unique reversal run identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Operator-facing run number (e.g. <c>RR-20260502-AB12CD</c>).</summary>
    [Required]
    [StringLength(50)]
    public string ReversalRunNumber { get; set; } = string.Empty;

    /// <summary>Optional operator-supplied description.</summary>
    public string? Description { get; set; }

    /// <summary>Run lifecycle status. Mirrors <see cref="PaymentRunStatus"/> enumeration order (Pending=0).</summary>
    [Required]
    public ReversalRunStatus Status { get; set; } = ReversalRunStatus.Pending;

    /// <summary>Filter criteria used to materialize the adjustment batch.</summary>
    public ReversalRunCriteria Criteria { get; set; } = new();

    /// <summary>
    /// 5.12a <c>ClaimAdjustment</c> ids that this run consumed. Populated
    /// during <c>ExecuteReversalRunAsync</c>. Each id corresponds to one
    /// successful Voided / AlreadyVoided outcome on the predecessor; ids
    /// that hit warning paths (InvalidSourceState / NotFound / 5xx) stay
    /// out of this list and are surfaced in <see cref="Warnings"/>.
    /// </summary>
    public List<string> AdjustmentIds { get; set; } = new();

    /// <summary>
    /// Negative-amount <c>Payment</c> records produced for the reversal
    /// envelopes. One <c>Payment</c> per trading-partner group; CLP02="22"
    /// per claim; CAS amounts sign-flipped.
    /// </summary>
    public List<string> PaymentIds { get; set; } = new();

    /// <summary>
    /// <see cref="EraEnvelopeRecord"/> ids produced by this run; each
    /// row's <see cref="EraEnvelopeRecord.ReversalRunId"/> points back at
    /// this run's <see cref="Id"/>.
    /// </summary>
    public List<string> EraEnvelopeIds { get; set; } = new();

    /// <summary>Total adjustments processed (success + warning combined).</summary>
    public int TotalAdjustments { get; set; }

    /// <summary>Total reversal amount (negative). Sum of envelope BPR02s.</summary>
    public decimal TotalReversalAmount { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Operator who created the run.</summary>
    [StringLength(100)]
    public string? CreatedBy { get; set; }

    /// <summary>UTC timestamp when execution started (Status → Running).</summary>
    public DateTime? ExecutionStartedAt { get; set; }

    /// <summary>UTC timestamp when execution finished (Completed / Failed).</summary>
    public DateTime? ExecutionCompletedAt { get; set; }

    /// <summary>Execution wall-clock duration in seconds.</summary>
    public double? ExecutionDurationSeconds { get; set; }

    /// <summary>Hard-stop errors (run lands in Failed when populated).</summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>Per-adjustment warning surface (run completes with warnings).</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Filter criteria for materializing the adjustment batch. The
/// claims-service <c>GET /api/v1/adjustments</c> surface natively
/// supports status + date-range + pagination; <see cref="ProviderNPI"/>
/// is applied as a post-fetch filter on the predecessor claim's
/// billing/pay-to NPI by <c>ReversalRunService</c>.
/// <see cref="AdjustmentIds"/> is the explicit-override path for
/// operator hand-curated batches and bypasses all other filters.
///
/// <para>
/// Phase 1 omits a Line-of-Business filter — the cross-service
/// <c>ClaimDto</c> shape doesn't carry LineOfBusiness today, so a
/// post-fetch LOB filter would silently drop everything. Phase 2 may
/// extend the claim wire shape and re-introduce the filter.
/// </para>
/// </summary>
public class ReversalRunCriteria
{
    /// <summary>
    /// Filter by billing/pay-to provider NPI on the predecessor claim.
    /// Applied post-fetch by <c>ReversalRunService</c> against
    /// <c>ClaimDto.PayToProviderNPI ?? ClaimDto.BillingProviderNPI</c>.
    /// </summary>
    [StringLength(10)]
    public string? ProviderNPI { get; set; }

    /// <summary>Filter adjustment <c>CreatedAt</c> >= AdjustmentDateFrom.</summary>
    public DateTime? AdjustmentDateFrom { get; set; }

    /// <summary>Filter adjustment <c>CreatedAt</c> &lt;= AdjustmentDateTo.</summary>
    public DateTime? AdjustmentDateTo { get; set; }

    /// <summary>
    /// Explicit override — when supplied, the run consumes only these
    /// adjustment ids regardless of the date/NPI filters above.
    /// Empty list (the default) means "use the filter criteria".
    /// </summary>
    public List<string> AdjustmentIds { get; set; } = new();
}

/// <summary>
/// Lifecycle states for <see cref="ReversalRun"/>. Mirrors
/// <see cref="PaymentRunStatus"/> ordinal-for-ordinal so serialization /
/// telemetry conventions match across PaymentRun + ReversalRun.
/// </summary>
public enum ReversalRunStatus
{
    /// <summary>Created but not yet executed.</summary>
    Pending = 0,
    /// <summary>Currently executing.</summary>
    Running = 1,
    /// <summary>Successfully completed (may have per-adjustment warnings).</summary>
    Completed = 2,
    /// <summary>Hit an unrecoverable error during execution.</summary>
    Failed = 3,
    /// <summary>Operator-cancelled before execution.</summary>
    Cancelled = 4,
}
