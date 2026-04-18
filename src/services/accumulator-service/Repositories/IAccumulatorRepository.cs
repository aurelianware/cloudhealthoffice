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
}

/// <summary>
/// Idempotency store for ClaimFinalized processing. Upsert-then-check on the
/// unique (tenantId, claimId) key — duplicate-claim detection is race-free.
/// </summary>
public interface IProcessedClaimStore
{
    /// <summary>
    /// Attempt to mark a claim as processed. Returns true when the marker was
    /// newly inserted (apply should proceed); false when the claim was already
    /// applied (consumer should skip). ResultingEventId is populated later by
    /// <see cref="CompleteAsync"/>.
    /// </summary>
    Task<bool> TryClaimAsync(string tenantId, string claimId, CancellationToken ct = default);

    Task CompleteAsync(string tenantId, string claimId, string resultingEventId, string outcome, CancellationToken ct = default);

    Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default);
}
