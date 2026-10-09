using AppealsService.Models;

namespace AppealsService.Repositories;

/// <summary>
/// Dispatcher-side access to the transactional outbox embedded in each
/// appeal document (<see cref="Appeal.Outbox"/>). Implemented by each
/// <see cref="IAppealRepository"/> over the same collection / container.
/// Every update here touches only outbox and lease fields, so it never
/// overwrites an appeal change (Mongo: targeted <c>$set</c>; Cosmos:
/// ETag-pinned replace with re-read on 412).
/// </summary>
public interface IAppealOutboxStore
{
    /// <summary>
    /// Appeals (any tenant) holding at least one <see cref="AppealOutboxStatus.Pending"/>
    /// entry. May over-report (e.g. entries not yet due, leased appeals):
    /// the dispatcher re-checks under the lease.
    /// </summary>
    Task<IReadOnlyList<AppealOutboxKey>> FindPendingAsync(DateTime now, int limit, CancellationToken ct = default);

    /// <summary>
    /// Take the per-appeal dispatch lease (free or expired) and return the
    /// appeal's outbox entries in order; <c>null</c> when another dispatcher
    /// holds a live lease or the appeal is gone.
    /// </summary>
    Task<IReadOnlyList<AppealOutboxMessage>?> TryLeaseAsync(
        string tenantId, string appealId, string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default);

    /// <summary>Release the lease (when still held by <paramref name="owner"/>) and prune completed entries older than <paramref name="pruneCompletedBefore"/>.</summary>
    Task ReleaseLeaseAsync(
        string tenantId, string appealId, string owner, DateTime pruneCompletedBefore, CancellationToken ct = default);

    /// <summary>
    /// Persist the dispatcher's outcome for one entry, matched by EventId:
    /// status, attempts, next attempt, last error and completion time.
    /// </summary>
    Task UpdateMessageAsync(
        string tenantId, string appealId, AppealOutboxMessage message, CancellationToken ct = default);

    /// <summary>
    /// Move <see cref="AppealOutboxStatus.DeadLettered"/> entries of one appeal
    /// (all of them, or the one with <paramref name="eventId"/>) back to
    /// <see cref="AppealOutboxStatus.Pending"/> with zero attempts. Returns
    /// how many were requeued.
    /// </summary>
    Task<int> RequeueDeadLetteredAsync(
        string tenantId, string appealId, string? eventId, CancellationToken ct = default);
}

public sealed record AppealOutboxKey(string TenantId, string AppealId);
