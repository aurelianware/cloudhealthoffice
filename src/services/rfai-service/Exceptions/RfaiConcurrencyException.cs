using CloudHealthOffice.Infrastructure.Data;

namespace RfaiService.Exceptions;

/// <summary>
/// An RFAI case write based on a stale read: another write changed the case
/// (its <see cref="Models.RfaiCase.Revision"/>) since it was read. Nothing was
/// written; 409 Conflict at the API boundary. The caller re-reads and retries.
/// </summary>
public sealed class RfaiConcurrencyException : ConcurrencyConflictException
{
    public RfaiConcurrencyException(string caseId, long expectedRevision)
        : base($"RFAI case {caseId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        CaseId = caseId;
        ExpectedRevision = expectedRevision;
    }

    public string CaseId { get; }
    public long ExpectedRevision { get; }
}
