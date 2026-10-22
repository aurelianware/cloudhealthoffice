using CloudHealthOffice.Infrastructure.Data;

namespace AuthorizationService.Exceptions;

/// <summary>
/// An inpatient-stay write based on a stale read: another write changed the
/// stay (its <see cref="Models.InpatientStay.Revision"/>) since it was read.
/// Two reviewers working the same census row is the ordinary way this
/// happens. Nothing was written; 409 Conflict at the API boundary, and the
/// caller re-reads and applies the review again.
/// </summary>
public sealed class InpatientStayConcurrencyException : ConcurrencyConflictException
{
    public InpatientStayConcurrencyException(string stayId, long expectedRevision)
        : base($"Inpatient stay {stayId} was changed by another write (expected revision {expectedRevision}); re-read it and apply the review again.")
    {
        StayId = stayId;
        ExpectedRevision = expectedRevision;
    }

    public string StayId { get; }
    public long ExpectedRevision { get; }
}
