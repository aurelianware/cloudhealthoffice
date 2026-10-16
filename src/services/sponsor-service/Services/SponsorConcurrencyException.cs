using CloudHealthOffice.Infrastructure.Data;

namespace SponsorService.Services;

/// <summary>
/// A sponsor write based on a stale read: another write (an edit, a status
/// change) changed the sponsor (its <see cref="Models.Sponsor.Revision"/>)
/// since it was read. Nothing was written; 409 Conflict at the API boundary.
/// The caller re-reads and retries.
/// </summary>
public sealed class SponsorConcurrencyException : ConcurrencyConflictException
{
    public SponsorConcurrencyException(string id, long expectedRevision)
        : base($"Sponsor {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        SponsorId = id;
        ExpectedRevision = expectedRevision;
    }

    public string SponsorId { get; }
    public long ExpectedRevision { get; }
}
