using EnrollmentImportService.Models;

namespace EnrollmentImportService.Repositories;

/// <summary>
/// Append-only store for <see cref="EnrollmentEvent"/>.
///
/// Writes are idempotent keyed on <c>(TenantId, MemberId, EventId)</c>. Repeated calls with
/// the same EventId return the existing event (<c>Appended=false</c>) without writing a
/// new document.
/// </summary>
public interface IEnrollmentEventRepository
{
    /// <summary>
    /// Append an event. Returns <c>Appended=false</c> with the existing event for an
    /// EventId collision, or <c>Appended=false</c> with the in-memory envelope (no Event)
    /// when a concurrent writer claimed our version slot.
    /// </summary>
    Task<EnrollmentEventAppendResult> AppendAsync(EnrollmentEvent evt, CancellationToken ct = default);

    /// <summary>
    /// List events for a member, ordered by <see cref="EnrollmentEvent.Version"/>
    /// <b>descending</b> (newest first) — this is what the controller + portal
    /// timeline consume. Optionally filtered by event type and/or occurredAt window.
    /// Callers that need chronological replay should reverse the page.
    /// </summary>
    Task<EnrollmentEventPage> ListByMemberAsync(
        string tenantId,
        string memberId,
        EnrollmentEventQuery query,
        CancellationToken ct = default);

    Task<EnrollmentEvent?> GetByIdAsync(
        string tenantId,
        string memberId,
        string eventId,
        CancellationToken ct = default);

    Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default);
}

public readonly record struct EnrollmentEventAppendResult(EnrollmentEvent Event, bool Appended);

public sealed record EnrollmentEventQuery(
    EnrollmentEventType? EventType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Limit = 100,
    string? ContinuationToken = null);

public sealed record EnrollmentEventPage(
    IReadOnlyList<EnrollmentEvent> Items,
    string? ContinuationToken);
