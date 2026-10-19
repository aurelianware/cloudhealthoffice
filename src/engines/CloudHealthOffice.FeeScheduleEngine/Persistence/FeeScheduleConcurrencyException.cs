using CloudHealthOffice.Infrastructure.Data;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// A fee schedule or provider contract upsert based on a stale read: the
/// stored document's revision is no longer the one the write was based on
/// (another replace happened in between, or a create found the document
/// already there). Nothing was written; 409 Conflict at the API boundary.
/// The caller re-reads and retries.
/// </summary>
public sealed class FeeScheduleConcurrencyException : ConcurrencyConflictException
{
    public FeeScheduleConcurrencyException(string entity, string id, long expectedRevision, Exception? inner = null)
        : base($"{entity} {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.", inner)
    {
        Entity = entity;
        Id = id;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>"Fee schedule" or "Provider contract".</summary>
    public string Entity { get; }
    public string Id { get; }
    public long ExpectedRevision { get; }
}
