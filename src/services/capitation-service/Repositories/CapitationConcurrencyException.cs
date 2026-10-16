using CloudHealthOffice.Infrastructure.Data;

namespace CapitationService.Repositories;

/// <summary>
/// A capitation run, statement, disbursement or contract write based on a stale read:
/// another write changed the document (its <c>Revision</c>) since it was read.
/// Nothing was written; 409 Conflict at the API boundary. The caller re-reads
/// and retries.
/// </summary>
public sealed class CapitationConcurrencyException : ConcurrencyConflictException
{
    public CapitationConcurrencyException(string entity, string id, long expectedRevision)
        : base($"{entity} {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        Entity = entity;
        Id = id;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>"Capitation run", "Capitation statement", "Capitation disbursement" or "Capitation contract".</summary>
    public string Entity { get; }
    public string Id { get; }
    public long ExpectedRevision { get; }
}
