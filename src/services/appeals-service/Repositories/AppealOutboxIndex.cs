using AppealsService.Models;

namespace AppealsService.Repositories;

/// <summary>
/// Recomputes the Cosmos-only top-level sweep fields on <see cref="Appeal"/>
/// (<see cref="Appeal.OutboxNextDueAt"/>, <see cref="Appeal.OutboxLeaseUntilMs"/>,
/// <see cref="Appeal.OutboxPendingCount"/>, <see cref="Appeal.OutboxOldestPendingAt"/>,
/// <see cref="Appeal.OutboxDeadLetteredCount"/>) from the outbox array. Called
/// on the document right before every Cosmos write that touches the outbox
/// or the lease, so the fields always describe the document they are in.
/// </summary>
public static class AppealOutboxIndex
{
    public static long ToEpochMs(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static void Refresh(Appeal appeal)
    {
        var outbox = appeal.Outbox ?? new List<AppealOutboxMessage>();
        var pending = outbox.Where(m => m.Status == AppealOutboxStatus.Pending).ToList();
        var deadLettered = outbox.Where(m => m.Status == AppealOutboxStatus.DeadLettered).ToList();

        var dueTimes = pending.Select(m => ToEpochMs(m.NextAttemptAt ?? m.CreatedAt))
            .Concat(deadLettered.Where(m => m.ExpiresAt.HasValue).Select(m => ToEpochMs(m.ExpiresAt!.Value)))
            .ToList();

        appeal.OutboxNextDueAt = dueTimes.Count > 0 ? dueTimes.Min() : null;
        appeal.OutboxPendingCount = pending.Count > 0 ? pending.Count : null;
        appeal.OutboxOldestPendingAt = pending.Count > 0 ? pending.Min(m => ToEpochMs(m.CreatedAt)) : null;
        appeal.OutboxDeadLetteredCount = deadLettered.Count > 0 ? deadLettered.Count : null;
        appeal.OutboxLeaseUntilMs = appeal.OutboxLeaseUntil is { } until ? ToEpochMs(until) : null;
    }

    /// <summary>
    /// True when the relay has work on this appeal at <paramref name="now"/>:
    /// a pending entry that is due, or a dead letter past its retention.
    /// </summary>
    public static bool HasDueWork(IEnumerable<AppealOutboxMessage>? outbox, DateTime now) =>
        outbox?.Any(m =>
            (m.Status == AppealOutboxStatus.Pending && (m.NextAttemptAt is null || m.NextAttemptAt <= now))
            || (m.Status == AppealOutboxStatus.DeadLettered && m.ExpiresAt is { } expires && expires <= now)) == true;
}
