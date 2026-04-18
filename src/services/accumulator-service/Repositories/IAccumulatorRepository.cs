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

    Task UpsertSnapshotAsync(AccumulatorSnapshot snapshot, CancellationToken ct = default);

    Task AppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default);

    Task<IReadOnlyList<AccumulatorEvent>> GetEventsAsync(string tenantId, string memberId, int take = 100, CancellationToken ct = default);

    /// <summary>
    /// Look up a prior <c>ManualAdjustment</c> event by its caller-supplied
    /// <c>AdjustmentId</c>. Used by <c>AdjustAsync</c> to make client-provided
    /// adjustment ids idempotent: a retry returns the existing snapshot rather
    /// than attempting a second apply that would 500 on the unique index.
    /// Returns null when no prior adjustment exists.
    /// </summary>
    Task<AccumulatorEvent?> GetManualAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default);
}

/// <summary>
/// Two-phase idempotency store for ClaimFinalized processing. A claim goes
/// Pending → Applied | OrphanSkipped. The lease-style design means a failure
/// between the begin-marker insert and the final CompleteAsync (DB or Kafka
/// hiccup) does NOT permanently mark the claim deduped — the next redelivery
/// sees a Pending marker and re-enters the apply path. Only a terminal
/// (Applied / OrphanSkipped) marker causes a skip.
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

    Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default);
}

public enum BeginClaimOutcome
{
    Proceed,
    AlreadyApplied
}
