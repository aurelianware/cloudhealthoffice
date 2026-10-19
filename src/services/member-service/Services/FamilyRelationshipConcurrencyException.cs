using CloudHealthOffice.Infrastructure.Data;

namespace MemberService.Services;

/// <summary>
/// A family-relationship pair update based on a stale read: another write
/// changed one of the two rows (its <see cref="Models.FamilyRelationship.Revision"/>)
/// since it was read. The transaction was aborted, so neither row was written;
/// 409 Conflict at the API boundary. The caller re-reads and retries.
/// </summary>
public sealed class FamilyRelationshipConcurrencyException : ConcurrencyConflictException
{
    public FamilyRelationshipConcurrencyException(string id, long expectedRevision, Exception? inner = null)
        : base($"Family relationship {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.", inner)
    {
        RelationshipId = id;
        ExpectedRevision = expectedRevision;
    }

    public string RelationshipId { get; }
    public long ExpectedRevision { get; }
}
