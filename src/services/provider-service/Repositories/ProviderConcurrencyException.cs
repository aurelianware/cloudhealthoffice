using CloudHealthOffice.Infrastructure.Data;

namespace ProviderService.Repositories;

/// <summary>
/// A provider or organization version write based on a stale read: another
/// write changed the version row (its <c>Revision</c> or its
/// <c>VersionState</c>) since it was read, for example a concurrent activation
/// of the same draft or a concurrent supersede of the same head version.
/// Nothing was written; 409 Conflict at the API boundary. The caller re-reads
/// and retries.
/// </summary>
public sealed class ProviderConcurrencyException : ConcurrencyConflictException
{
    public ProviderConcurrencyException(string entity, string id, long expectedRevision, Exception? inner = null)
        : base($"{entity} version {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.", inner)
    {
        Entity = entity;
        DocumentId = id;
        ExpectedRevision = expectedRevision;
    }

    /// <summary><c>Provider</c> or <c>Organization</c>.</summary>
    public string Entity { get; }

    /// <summary>The version row's document id.</summary>
    public string DocumentId { get; }
    public long ExpectedRevision { get; }
}
