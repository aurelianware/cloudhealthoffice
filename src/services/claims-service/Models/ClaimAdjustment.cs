using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// Operator-initiated adjustment aggregate (capability 5.12). One row per
/// adjustment workflow tracks the cross-version transition: a predecessor
/// version is superseded, a new version is created with
/// <see cref="Claim.PredecessorVersionId"/> set to the predecessor, the
/// new version re-runs the full 6-stage adjudication pipeline (5.4–5.9),
/// and (in 5.12b) a payment-service ReversalRun batches the predecessor
/// payment reversal + 835 reversal envelope.
///
/// <para>
/// Lifecycle (ratified Decision 18 — corrects original prompt's inverted
/// order): <c>AwaitingReadjudication</c> → <c>PendingReversal</c> →
/// <c>Active</c>. <c>Failed</c> is a terminal off-path state if any step
/// hits an unrecoverable error. The new version's pipeline runs
/// asynchronously via the existing Service Bus
/// <c>claim-version-events</c> subscription, so there is a real interval
/// between supersession and pipeline completion that operators see as
/// <c>AwaitingReadjudication</c>; once the new version reaches
/// Adjudicated/Paid the adjustment transitions to <c>PendingReversal</c>
/// (waiting for ReversalRun to actually unwind the predecessor's
/// accumulator impact).
/// </para>
///
/// <para>
/// Per Decision 14 we deliberately do NOT add an
/// <c>AdjustmentPending</c> value to <see cref="ClaimStatus"/>; per-claim
/// status stays unchanged so the legacy 22 controller endpoints + the
/// accumulator-service Kafka contract are not perturbed. Adjustment
/// state is captured solely on this aggregate.
/// </para>
///
/// <para>
/// Per Decision 11 the adjustment chain depth is capped at 1 in Phase 1.
/// The uniqueness key is <c>(TenantId, ClaimVersionId)</c> — at most one
/// in-flight adjustment per claim chain. Adjustment-of-adjustment is
/// deferred to Phase 2 (which will widen the key to include a generation
/// field).
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimAdjustment
{
    [Required]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Chain key — same value as the predecessor's
    /// <see cref="Claim.ClaimVersionId"/> AND the new version's
    /// <c>ClaimVersionId</c>. Both versions of the claim share this
    /// identifier; the per-row <see cref="Claim.Id"/>s differ. The unique
    /// index <c>(TenantId, ClaimVersionId)</c> enforces depth=1.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string ClaimVersionId { get; set; } = string.Empty;

    /// <summary>The predecessor claim row id (the version being adjusted).</summary>
    [Required]
    [StringLength(64)]
    public string PredecessorClaimId { get; set; } = string.Empty;

    /// <summary>The predecessor's per-version VersionId (same as PredecessorClaimId today; preserved separately for forward-compat).</summary>
    [Required]
    [StringLength(64)]
    public string PredecessorVersionId { get; set; } = string.Empty;

    /// <summary>The new (replacement) claim row id created by the adjustment.</summary>
    [Required]
    [StringLength(64)]
    public string NewClaimId { get; set; } = string.Empty;

    /// <summary>Operator-supplied reason for the adjustment (audit context).</summary>
    [Required]
    [StringLength(500)]
    public string AdjustmentReason { get; set; } = string.Empty;

    /// <summary>Optional free-text notes from the operator.</summary>
    [StringLength(2000)]
    public string? Notes { get; set; }

    [Required]
    public ClaimAdjustmentStatus Status { get; set; } = ClaimAdjustmentStatus.AwaitingReadjudication;

    /// <summary>
    /// Operator-supplied idempotency key (per Decision 6). Same key + same
    /// body returns the existing adjustment with 200; same key + different
    /// body returns 409 Conflict; different key creates a new adjustment.
    /// Stored to support replay deduplication across pod restarts.
    /// </summary>
    [Required]
    [StringLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Stable hash of the request body that produced this adjustment.
    /// Used together with <see cref="IdempotencyKey"/> to detect
    /// "same key, different body" 409s.
    /// </summary>
    [Required]
    [StringLength(128)]
    public string RequestHash { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set when the new version's pipeline reaches a terminal Adjudicated/Paid/Denied
    /// state and the adjustment transitions to <see cref="ClaimAdjustmentStatus.PendingReversal"/>.
    /// </summary>
    public DateTime? ReadjudicationCompletedAt { get; set; }

    /// <summary>Set when the 5.12b ReversalRun completes; transitions to <see cref="ClaimAdjustmentStatus.Active"/>.</summary>
    public DateTime? ReversalCompletedAt { get; set; }

    /// <summary>FK to payment-service ReversalRun (populated in 5.12b execution path).</summary>
    [StringLength(64)]
    public string? ReversalRunId { get; set; }

    /// <summary>Free-text failure reason captured when the adjustment lands in <see cref="ClaimAdjustmentStatus.Failed"/>.</summary>
    [StringLength(2000)]
    public string? FailureReason { get; set; }
}

/// <summary>
/// Lifecycle states for <see cref="ClaimAdjustment"/>. Order ratified by
/// Decision 18 — re-adjudication runs first (asynchronously via the existing
/// Service Bus pipeline), then ReversalRun (5.12b) batches the predecessor
/// reversal. The original prompt's inverted order
/// (PendingReversal → AwaitingReadjudication) was caught at plan phase.
/// </summary>
public enum ClaimAdjustmentStatus
{
    /// <summary>
    /// Predecessor superseded; new version persisted with
    /// <see cref="Claim.PredecessorVersionId"/> set; pipeline running
    /// asynchronously. Most adjustments leave this state within seconds.
    /// </summary>
    AwaitingReadjudication = 1,

    /// <summary>
    /// New version reached terminal pipeline state; predecessor still has
    /// active accumulator impact + provider payment; awaiting 5.12b
    /// ReversalRun to unwind. Operator-batched via
    /// <c>POST /api/v1/reversal-runs</c>.
    /// </summary>
    PendingReversal = 2,

    /// <summary>
    /// ReversalRun completed; predecessor accumulators reversed via BP
    /// engine (Decision 16); predecessor claim Voided; reversal 835 emitted.
    /// Terminal happy-path state.
    /// </summary>
    Active = 3,

    /// <summary>
    /// Off-path terminal state when any step (supersession write, pipeline
    /// run, reversal call, void transition) hit an unrecoverable error.
    /// Manual operator triage required; <see cref="ClaimAdjustment.FailureReason"/>
    /// carries the diagnostic.
    /// </summary>
    Failed = 4,
}
