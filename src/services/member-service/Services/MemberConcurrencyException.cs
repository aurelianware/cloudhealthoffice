using CloudHealthOffice.Infrastructure.Data;

namespace MemberService.Services;

/// <summary>
/// A member write based on a stale read: another write changed the member (its
/// <see cref="Models.Member.Revision"/>) since it was read. Nothing was
/// written; 409 Conflict at the API boundary. The caller re-reads and retries.
/// Not <see cref="ConcurrencyException"/>, which is the event publisher's
/// exhausted-retries signal.
/// </summary>
public sealed class MemberConcurrencyException : ConcurrencyConflictException
{
    public MemberConcurrencyException(string id, long expectedRevision)
        : base($"Member {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        MemberDocumentId = id;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>The member document id (<see cref="Models.Member.Id"/>).</summary>
    public string MemberDocumentId { get; }
    public long ExpectedRevision { get; }
}
