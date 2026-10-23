using ContactService.Models;

namespace ContactService.Services.Claims;

/// <summary>
/// Writes the "a person called about this" note on the thing that was
/// discussed, so its own history shows the call without anyone retyping it
/// (cho-service.md §3, "Notes on claims").
///
/// Claims are the only subject that takes one today. Claim notes
/// (claims-service) are append-only, categorised and attributed, which is
/// exactly the shape this needs. Authorizations carry a single free-text
/// <c>Notes</c> field appended on status change, not a note history, so
/// writing a contact note there would be a change to authorization-service,
/// not a call from here; it is left for when authorizations grow real notes.
/// Appeals, payment runs and documents take none.
/// </summary>
public interface IContactNoteGateway
{
    /// <summary>
    /// Writes the note and returns its id, or null when the subject takes no
    /// note or the write did not succeed. Never throws for a failed write:
    /// the contact is the record of the conversation and stands on its own,
    /// so a claims-service that is down loses the cross-reference, not the
    /// call.
    /// </summary>
    Task<string?> WriteContactNoteAsync(ContactNoteCommand command, CancellationToken ct = default);
}

/// <summary>What to write, and on what.</summary>
/// <param name="TenantId">The contact's own tenant.</param>
/// <param name="SubjectType">What is being annotated.</param>
/// <param name="SubjectId">Its id in its own service.</param>
/// <param name="CallerType">Who was on the call, which picks the note category.</param>
/// <param name="ContactNumber">The contact's number, named in the note so the two can be found from each other.</param>
/// <param name="Note">What the caller asked about this subject; may be empty.</param>
public sealed record ContactNoteCommand(
    string TenantId,
    ContactSubjectType SubjectType,
    string SubjectId,
    CallerType CallerType,
    string ContactNumber,
    string? Note);

/// <summary>
/// Used when no claims-service is configured. The link is still recorded on
/// the contact; nothing is written on the claim.
/// </summary>
public sealed class NullContactNoteGateway : IContactNoteGateway
{
    public Task<string?> WriteContactNoteAsync(ContactNoteCommand command, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
