using CloudHealthOffice.Infrastructure.Data;

namespace EncounterService.Repositories;

/// <summary>
/// An encounter write based on a stale read: another write changed the
/// encounter (its <see cref="Models.Encounter.Revision"/>) since it was read.
/// Nothing was written; 409 Conflict at the API boundary. The caller re-reads
/// and retries.
/// </summary>
public sealed class EncounterConcurrencyException : ConcurrencyConflictException
{
    public EncounterConcurrencyException(string encounterId, long expectedRevision)
        : base($"Encounter {encounterId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        EncounterId = encounterId;
        ExpectedRevision = expectedRevision;
    }

    public string EncounterId { get; }
    public long ExpectedRevision { get; }
}
