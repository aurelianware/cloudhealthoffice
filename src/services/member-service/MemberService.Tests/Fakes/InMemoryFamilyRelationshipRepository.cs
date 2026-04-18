using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>
/// In-memory fake for <see cref="IFamilyRelationshipRepository"/>. Emulates the
/// same-tenant / symmetric-pair invariants enforced by the Cosmos and Mongo impls
/// so service-layer tests can exercise the full behavior contract.
/// </summary>
public sealed class InMemoryFamilyRelationshipRepository : IFamilyRelationshipRepository
{
    public List<FamilyRelationship> Rows { get; } = new();

    /// <summary>If true, the next paired write throws after the first row is inserted —
    /// used to assert the atomic-pair contract.</summary>
    public bool SimulatePairTornWrite { get; set; }

    public Task CreatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default)
    {
        if (forward.TenantId != inverse.TenantId)
            throw new InvalidOperationException("Pair rows must share TenantId.");

        if (SimulatePairTornWrite)
        {
            // Write only the forward row, then throw. Tests assert the service /
            // higher-level caller's expectation that a torn write is observable via
            // the rogue row being left behind — i.e., the repo itself is NOT atomic
            // when this flag is set. Use this to exercise failure-path code paths
            // that should clean up or surface the inconsistency.
            Rows.Add(Clone(forward));
            throw new InvalidOperationException("Simulated transactional failure.");
        }

        Rows.Add(Clone(forward));
        Rows.Add(Clone(inverse));
        return Task.CompletedTask;
    }

    public Task UpdatePairAsync(FamilyRelationship forward, FamilyRelationship inverse, CancellationToken ct = default)
    {
        if (forward.TenantId != inverse.TenantId)
            throw new InvalidOperationException("Pair rows must share TenantId.");

        Replace(forward);
        Replace(inverse);
        return Task.CompletedTask;
    }

    public Task<FamilyRelationship?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult<FamilyRelationship?>(
            Rows.FirstOrDefault(r => r.TenantId == tenantId && r.Id == id));

    public Task<List<FamilyRelationship>> GetPairAsync(string tenantId, string pairId, CancellationToken ct = default)
        => Task.FromResult(Rows
            .Where(r => r.TenantId == tenantId && r.PairId == pairId)
            .ToList());

    public Task<List<FamilyRelationship>> ListBySubjectAsync(
        string tenantId, string subjectMemberId, bool includeDeleted = false, CancellationToken ct = default)
        => Task.FromResult(Rows
            .Where(r => r.TenantId == tenantId && r.SubjectMemberId == subjectMemberId)
            .Where(r => includeDeleted || r.DeletedAt == null)
            .ToList());

    public Task<List<FamilyRelationship>> ListTouchingAsync(
        string tenantId, string memberId, bool includeDeleted = false, CancellationToken ct = default)
        => Task.FromResult(Rows
            .Where(r => r.TenantId == tenantId &&
                        (r.SubjectMemberId == memberId || r.RelatedMemberId == memberId))
            .Where(r => includeDeleted || r.DeletedAt == null)
            .ToList());

    public Task<FamilyRelationship?> FindActivePairAsync(
        string tenantId, string subjectMemberId, string relatedMemberId, CancellationToken ct = default)
    {
        // Match production semantics: a future EndDate is still "active".
        var now = DateTime.UtcNow;
        return Task.FromResult<FamilyRelationship?>(
            Rows.FirstOrDefault(r =>
                r.TenantId == tenantId &&
                r.SubjectMemberId == subjectMemberId &&
                r.RelatedMemberId == relatedMemberId &&
                r.DeletedAt == null &&
                (r.EndDate == null || r.EndDate > now)));
    }

    private void Replace(FamilyRelationship row)
    {
        var idx = Rows.FindIndex(r => r.TenantId == row.TenantId && r.Id == row.Id);
        if (idx < 0) Rows.Add(Clone(row));
        else Rows[idx] = Clone(row);
    }

    private static FamilyRelationship Clone(FamilyRelationship r) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        SubjectMemberId = r.SubjectMemberId,
        RelatedMemberId = r.RelatedMemberId,
        RelationshipCode = r.RelationshipCode,
        PairId = r.PairId,
        StartDate = r.StartDate,
        EndDate = r.EndDate,
        IsCustodial = r.IsCustodial,
        QmcsoReference = r.QmcsoReference,
        CreatedDate = r.CreatedDate,
        CreatedBy = r.CreatedBy,
        UpdatedDate = r.UpdatedDate,
        UpdatedBy = r.UpdatedBy,
        DeletedAt = r.DeletedAt,
        DeletedBy = r.DeletedBy,
        DeletedReason = r.DeletedReason,
    };
}
