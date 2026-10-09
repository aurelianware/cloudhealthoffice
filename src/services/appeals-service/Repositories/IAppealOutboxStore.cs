using AppealsService.Models;

namespace AppealsService.Repositories;

/// <summary>
/// Dispatcher-side access to the transactional outbox embedded in each
/// appeal document (<see cref="Appeal.Outbox"/>). Implemented by each
/// <see cref="IAppealRepository"/> over the same collection / container.
/// Every update here touches only outbox and lease fields, so it never
/// overwrites an appeal change (Mongo: targeted <c>$set</c>; Cosmos:
/// ETag-pinned replace with re-read on 412).
///
/// Every per-entry write is conditioned on the caller still holding the
/// lease, on the entry's server-generated <see cref="AppealOutboxMessage.Id"/>
/// and on the entry still being <see cref="AppealOutboxStatus.Pending"/>, so
/// a stale lease holder can never overwrite another dispatcher's result.
/// </summary>
public interface IAppealOutboxStore
{
    /// <summary>
    /// Appeals (any tenant) with relay work due at <paramref name="now"/> — a
    /// due pending entry or a dead letter past retention — and no live lease.
    /// </summary>
    Task<IReadOnlyList<AppealOutboxKey>> FindDueAsync(DateTime now, int limit, CancellationToken ct = default);

    /// <summary>
    /// Take the per-appeal dispatch lease (free, expired or already ours) —
    /// only when the appeal has due work — and return its outbox entries in
    /// order with the last assigned sequence. <c>null</c>: leased elsewhere,
    /// nothing due, or gone.
    /// </summary>
    Task<AppealOutboxLease?> TryLeaseAsync(
        string tenantId, string appealId, string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default);

    /// <summary>
    /// Extend the lease (still held by <paramref name="owner"/>) and, when
    /// <paramref name="assign"/> is given, record that entry's sequence and
    /// advance the appeal's counter — conditional on the counter being at
    /// <c>assign.Sequence - 1</c> and the entry having none. False: the lease
    /// (or the counter) moved on, and the caller must stop.
    /// </summary>
    Task<bool> RenewLeaseAsync(
        string tenantId, string appealId, string owner, DateTime leaseUntil,
        AppealOutboxSequenceAssignment? assign, CancellationToken ct = default);

    /// <summary>
    /// Persist the dispatcher's outcome for one entry: status, attempts, next
    /// attempt, last error, completion and expiry. Matches the entry by
    /// <see cref="AppealOutboxMessage.Id"/> while it is still pending and the
    /// lease is still <paramref name="owner"/>'s; false otherwise.
    /// </summary>
    Task<bool> UpdateMessageAsync(
        string tenantId, string appealId, string owner, AppealOutboxMessage message, CancellationToken ct = default);

    /// <summary>
    /// Release the lease (when still held by <paramref name="owner"/>) and
    /// remove the entries in <paramref name="pruneIds"/> unless they are
    /// pending (a concurrent replay may have revived one).
    /// </summary>
    Task ReleaseLeaseAsync(
        string tenantId, string appealId, string owner, IReadOnlyCollection<string> pruneIds, CancellationToken ct = default);

    /// <summary>
    /// Move <see cref="AppealOutboxStatus.DeadLettered"/> entries of one appeal
    /// (all, or the one whose wire event id or idempotency key is
    /// <paramref name="eventId"/>) back to pending with zero attempts. Returns
    /// how many were requeued.
    /// </summary>
    Task<int> RequeueDeadLetteredAsync(
        string tenantId, string appealId, string? eventId, CancellationToken ct = default);

    /// <summary>
    /// Bulk replay: requeue every dead-lettered entry in one tenant, or in
    /// every tenant when <paramref name="tenantId"/> is null.
    /// </summary>
    Task<int> RequeueAllDeadLetteredAsync(string? tenantId, CancellationToken ct = default);

    /// <summary>Backlog across all tenants, for the pending / oldest-pending gauges.</summary>
    Task<AppealOutboxStats> GetStatsAsync(CancellationToken ct = default);
}

public sealed record AppealOutboxKey(string TenantId, string AppealId);

/// <summary>A held lease: the outbox snapshot and the appeal's last assigned sequence.</summary>
public sealed record AppealOutboxLease(IReadOnlyList<AppealOutboxMessage> Entries, long LastSequence);

public sealed record AppealOutboxSequenceAssignment(string EntryId, long Sequence);

public sealed record AppealOutboxStats(long Pending, DateTime? OldestPendingCreatedAt);
