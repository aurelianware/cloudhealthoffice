using AccumulatorService.Models;

namespace AccumulatorService.Repositories;

/// <summary>
/// Persistence boundary for accumulator state. Split into two repositories by
/// aggregate (Snapshot vs Event) so the event stream can grow independently of
/// the snapshot read model. Both implementations (Mongo, Cosmos) partition by
/// tenantId.
/// </summary>
public interface IAccumulatorRepository
{
    Task<AccumulatorSnapshot?> GetSnapshotAsync(string tenantId, string memberId, DateTime planYearStart, CancellationToken ct = default);

    /// <summary>Snapshot covering the given as-of date, by plan year. Returns null when no matching snapshot exists.</summary>
    Task<AccumulatorSnapshot?> GetSnapshotByAsOfDateAsync(string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default);

    Task<IReadOnlyList<AccumulatorSnapshot>> GetSnapshotsAsync(string tenantId, string memberId, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="snapshot"/> only if the stored snapshot is still
    /// at <paramref name="expectedVersion"/> (0 = it must not exist yet).
    /// Returns false when another writer got there first; the caller re-reads
    /// and retries. Every snapshot write goes through this, so two writers
    /// cannot both write over the same version (PR #1278 re-review N5).
    /// </summary>
    Task<bool> TryReplaceSnapshotAsync(AccumulatorSnapshot snapshot, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Appends an event row. Returns false (and writes nothing) when a row
    /// with the same id, the same (tenantId, aggregateId, version), or —
    /// for a <c>ClaimReversed</c> row — the same (tenantId, sourceClaimId)
    /// already exists: another writer took that version, or already
    /// reversed the claim.
    /// </summary>
    Task<bool> TryAppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default);

    /// <summary>
    /// The snapshot's event rows with a version above <paramref name="afterVersion"/>,
    /// in version order. Used to project rows a crashed writer appended but
    /// never projected, and to replay the log for a legacy (unclamped) row.
    /// </summary>
    Task<IReadOnlyList<AccumulatorEvent>> GetAggregateEventsAsync(
        string tenantId, string aggregateId, long afterVersion = 0, CancellationToken ct = default);

    Task<IReadOnlyList<AccumulatorEvent>> GetEventsAsync(string tenantId, string memberId, int take = 100, CancellationToken ct = default);

    /// <summary>
    /// Look up a prior <c>ManualAdjustment</c> event by its caller-supplied
    /// <c>AdjustmentId</c>. Used by <c>AdjustAsync</c> to make client-provided
    /// adjustment ids idempotent: a retry returns the existing snapshot rather
    /// than attempting a second apply that would 500 on the unique index.
    /// Returns null when no prior adjustment exists.
    /// </summary>
    Task<AccumulatorEvent?> GetManualAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default);

    /// <summary>
    /// The <c>ClaimApplied</c> event a finalized claim produced (the deltas it
    /// actually applied), or null when the claim never applied. Used to
    /// reverse a voided or replaced claim.
    /// </summary>
    Task<AccumulatorEvent?> GetClaimAppliedEventAsync(string tenantId, string claimId, CancellationToken ct = default);

    /// <summary>The <c>ClaimReversed</c> event for a claim, or null when it was never reversed.</summary>
    Task<AccumulatorEvent?> GetClaimReversedEventAsync(string tenantId, string claimId, CancellationToken ct = default);
}

/// <summary>
/// Two-phase idempotency store for ClaimFinalized processing. A claim goes
/// Pending → Applied | OrphanSkipped. The lease-style design means a failure
/// between the begin-marker insert and the final CompleteAsync (DB or Kafka
/// hiccup) does NOT permanently mark the claim deduped — a redelivery after
/// the lease expires (<see cref="ProcessedClaimLease.Timeout"/>) takes the
/// Pending marker over and re-enters the apply path. A Pending marker younger
/// than the lease is another worker still in flight: the caller gets
/// <see cref="BeginClaimOutcome.InProgress"/> and retries later rather than
/// applying (or reversing) the claim a second time. Only a terminal
/// (Applied / OrphanSkipped / Reversed) marker causes a skip.
/// </summary>
public interface IProcessedClaimStore
{
    /// <summary>
    /// Outcome of attempting to begin processing a claim.
    ///
    /// <list type="bullet">
    ///   <item><description><c>Proceed</c>: caller owns the lease and should apply. Either the marker did not exist or an earlier attempt crashed before completing — the existing Pending row is treated as available for retry.</description></item>
    ///   <item><description><c>AlreadyApplied</c>: a prior call completed successfully. Caller must skip.</description></item>
    /// </list>
    /// </summary>
    Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default);

    Task CompleteAsync(string tenantId, string claimId, string resultingEventId, string outcome, CancellationToken ct = default);

    /// <summary>
    /// Deletes a still-Pending marker this caller took and is not going to
    /// complete (it found it must wait), so a retry can take it at once
    /// instead of waiting out the lease. A terminal marker is left alone.
    /// </summary>
    Task ReleaseAsync(string tenantId, string claimId, CancellationToken ct = default);

    Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default);
}

public enum BeginClaimOutcome
{
    Proceed,
    AlreadyApplied,

    /// <summary>
    /// A Pending marker younger than the lease: another attempt is in flight.
    /// The caller must not apply; it retries later (the Kafka consumer does
    /// not commit the offset).
    /// </summary>
    InProgress
}

/// <summary>Lease settings for <see cref="IProcessedClaimStore"/> Pending markers.</summary>
public static class ProcessedClaimLease
{
    /// <summary>
    /// How long a Pending marker means "in flight". After it, the attempt is
    /// presumed crashed and a retry takes the marker over (atomically, so only
    /// one retry wins). Comfortably longer than one apply.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
}
