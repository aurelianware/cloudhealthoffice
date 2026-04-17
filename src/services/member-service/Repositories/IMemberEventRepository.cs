using MemberService.Models;

namespace MemberService.Repositories;

/// <summary>
/// Append-only store for member events.
///
/// Writes are idempotent keyed on <c>(TenantId, MemberId, EventId)</c>. A repeated
/// <see cref="AppendAsync(MemberEvent, CancellationToken)"/> with the same
/// <see cref="MemberEvent.EventId"/> is a no-op that returns the previously-stored event.
///
/// Reads return events for a single member ordered by <see cref="MemberEvent.Version"/>.
/// </summary>
public interface IMemberEventRepository
{
    /// <summary>
    /// Append an event. If an event with the same <c>(TenantId, MemberId, EventId)</c>
    /// already exists, returns the existing event without writing a new document
    /// (<c>Appended=false</c>).
    /// </summary>
    Task<AppendResult> AppendAsync(MemberEvent evt, CancellationToken ct = default);

    /// <summary>
    /// List events for a member, ordered by <see cref="MemberEvent.Version"/> ascending.
    /// </summary>
    Task<IReadOnlyList<MemberEvent>> ListByMemberAsync(
        string tenantId,
        string memberId,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch a single event by <c>EventId</c>, scoped by <c>(TenantId, MemberId)</c>.
    /// </summary>
    Task<MemberEvent?> GetByIdAsync(
        string tenantId,
        string memberId,
        string eventId,
        CancellationToken ct = default);

    /// <summary>
    /// Return the next version number to use for the given member (max+1, or 1 if none).
    /// </summary>
    Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default);
}

public readonly record struct AppendResult(MemberEvent Event, bool Appended);
