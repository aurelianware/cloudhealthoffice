using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;

namespace MemberService.Repositories;

/// <summary>
/// Persistence contract for <see cref="FamilyRelationship"/>. Writes are atomic
/// symmetric pairs (A→B and B→A persisted together under a shared <c>PairId</c>).
///
/// Read methods exclude soft-deleted rows by default. Pass <c>includeDeleted=true</c>
/// to surface them for audit/admin reads.
/// </summary>
public interface IFamilyRelationshipRepository
{
    /// <summary>
    /// Atomically persist the forward and inverse rows of a symmetric relationship.
    /// Cosmos uses <c>TransactionalBatch</c> (both rows share <c>tenantId</c>); Mongo
    /// uses a session with a transaction.
    /// </summary>
    Task CreatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default);

    /// <summary>
    /// Atomically replace both rows of a pair (shared PairId). Caller is responsible
    /// for preserving symmetry — service layer enforces this.
    /// </summary>
    Task UpdatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default);

    Task<FamilyRelationship?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Returns all rows with the given PairId (normally exactly two).</summary>
    Task<List<FamilyRelationship>> GetPairAsync(string tenantId, string pairId, CancellationToken ct = default);

    /// <summary>
    /// All relationships where <paramref name="subjectMemberId"/> is the subject
    /// (the "from" side of the edge). Excludes soft-deleted unless requested.
    /// </summary>
    Task<List<FamilyRelationship>> ListBySubjectAsync(
        string tenantId, string subjectMemberId, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>
    /// All relationships touching <paramref name="memberId"/> on either side
    /// (subject OR related). Used for the portal Family tab and graph derivation.
    /// </summary>
    Task<List<FamilyRelationship>> ListTouchingAsync(
        string tenantId, string memberId, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>
    /// Look up an active (not ended, not deleted) relationship between two members. Used for
    /// duplicate-pair rejection on create.
    /// </summary>
    Task<FamilyRelationship?> FindActivePairAsync(
        string tenantId, string subjectMemberId, string relatedMemberId, CancellationToken ct = default);
}
