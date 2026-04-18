using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Services;

/// <summary>
/// Enforces the invariants of the family-relationship graph:
///
///  1. Symmetric graph — every write produces both A→B and B→A rows under a shared
///     <c>PairId</c>, via a transactional repository method.
///  2. Same-tenant — cross-tenant relationships are rejected in this phase.
///     (Deferred to a future phase that handles dual-coverage spouses on different
///     employer plans at the same payer.)
///  3. Code validation — relationship codes are validated at the service boundary
///     against <see cref="FamilyRelationshipCodes"/>. Unknown codes are rejected.
///  4. Soft-delete only — normal wind-down is end-dating; hard delete never occurs.
/// </summary>
public class FamilyRelationshipService : IFamilyRelationshipService
{
    /// <summary>Subset of X12 INS02 codes that indicate "dependent → subscriber".</summary>
    private static readonly HashSet<string> DependentToSubscriberCodes = new() { "19", "17", "10", "01", "53", "22", "29" };

    private readonly IFamilyRelationshipRepository _repo;
    private readonly IMemberRepository _memberRepo;

    public FamilyRelationshipService(IFamilyRelationshipRepository repo, IMemberRepository memberRepo)
    {
        _repo = repo;
        _memberRepo = memberRepo;
    }

    public async Task<FamilyRelationship> CreateAsync(
        string tenantId, CreateFamilyRelationshipRequest req, string? actor, CancellationToken ct = default)
    {
        if (req == null) throw new ArgumentNullException(nameof(req));

        if (string.IsNullOrWhiteSpace(req.SubjectMemberId) || string.IsNullOrWhiteSpace(req.RelatedMemberId))
            throw new FamilyRelationshipValidationException("subjectMemberId and relatedMemberId are required.");

        if (string.Equals(req.SubjectMemberId, req.RelatedMemberId, StringComparison.Ordinal))
            throw new FamilyRelationshipValidationException("A member cannot have a relationship to themselves.");

        if (!FamilyRelationshipCodes.IsValid(req.RelationshipCode))
            throw new FamilyRelationshipValidationException(
                $"Unknown relationshipCode '{req.RelationshipCode}'. See X12 INS02.");

        var inverseCode = FamilyRelationshipCodes.Invert(req.RelationshipCode)
            ?? throw new FamilyRelationshipValidationException(
                $"No inverse mapping for relationshipCode '{req.RelationshipCode}'.");

        // Same-tenant constraint: member lookups below run scoped to tenantId. If either
        // member is absent in this tenant, we treat it as a not-found.
        var subject = await _memberRepo.GetByMemberIdAsync(tenantId, req.SubjectMemberId);
        if (subject == null)
            throw new FamilyRelationshipValidationException($"Subject '{req.SubjectMemberId}' not found in tenant.");

        var related = await _memberRepo.GetByMemberIdAsync(tenantId, req.RelatedMemberId);
        if (related == null)
            throw new FamilyRelationshipValidationException($"Related member '{req.RelatedMemberId}' not found in tenant.");

        // Duplicate-active guard: surface a clear error rather than producing two
        // parallel active pairs, which would break "active" derivation downstream.
        var existing = await _repo.FindActivePairAsync(tenantId, req.SubjectMemberId, req.RelatedMemberId, ct);
        if (existing != null)
            throw new DuplicateFamilyRelationshipException(
                $"An active relationship already exists between '{req.SubjectMemberId}' and '{req.RelatedMemberId}'.");

        if (req.EndDate.HasValue && req.EndDate.Value < req.StartDate)
            throw new FamilyRelationshipValidationException("endDate must be on or after startDate.");

        var pairId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var forward = new FamilyRelationship
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            SubjectMemberId = req.SubjectMemberId,
            RelatedMemberId = req.RelatedMemberId,
            RelationshipCode = req.RelationshipCode,
            PairId = pairId,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            IsCustodial = req.IsCustodial,
            QmcsoReference = req.QmcsoReference,
            CreatedDate = now,
            CreatedBy = actor,
        };

        var inverse = new FamilyRelationship
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            SubjectMemberId = req.RelatedMemberId,
            RelatedMemberId = req.SubjectMemberId,
            RelationshipCode = inverseCode,
            PairId = pairId,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            IsCustodial = req.IsCustodial,
            QmcsoReference = req.QmcsoReference,
            CreatedDate = now,
            CreatedBy = actor,
        };

        await _repo.CreatePairAsync(forward, inverse, ct);
        return forward;
    }

    public async Task<FamilyRelationship> UpdateAsync(
        string tenantId, string id, UpdateFamilyRelationshipRequest req, string? actor, CancellationToken ct = default)
    {
        var (forward, inverse) = await LoadPairOrThrowAsync(tenantId, id, ct);

        if (req.StartDate.HasValue)
        {
            forward.StartDate = req.StartDate.Value;
            inverse.StartDate = req.StartDate.Value;
        }
        if (req.EndDate.HasValue)
        {
            if (req.EndDate.Value < (req.StartDate ?? forward.StartDate))
                throw new FamilyRelationshipValidationException("endDate must be on or after startDate.");
            forward.EndDate = req.EndDate.Value;
            inverse.EndDate = req.EndDate.Value;
        }
        if (req.IsCustodial.HasValue)
        {
            forward.IsCustodial = req.IsCustodial.Value;
            inverse.IsCustodial = req.IsCustodial.Value;
        }
        if (req.QmcsoReference != null)
        {
            forward.QmcsoReference = req.QmcsoReference;
            inverse.QmcsoReference = req.QmcsoReference;
        }

        StampUpdate(forward, actor);
        StampUpdate(inverse, actor);
        await _repo.UpdatePairAsync(forward, inverse, ct);
        return forward;
    }

    public async Task<FamilyRelationship> EndAsync(
        string tenantId, string id, DateTime endDate, string? actor, CancellationToken ct = default)
    {
        var (forward, inverse) = await LoadPairOrThrowAsync(tenantId, id, ct);

        if (endDate < forward.StartDate)
            throw new FamilyRelationshipValidationException("endDate must be on or after startDate.");

        forward.EndDate = endDate;
        inverse.EndDate = endDate;
        StampUpdate(forward, actor);
        StampUpdate(inverse, actor);
        await _repo.UpdatePairAsync(forward, inverse, ct);
        return forward;
    }

    public async Task<FamilyRelationship> SoftDeleteAsync(
        string tenantId, string id, string reason, string? actor,
        TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var (forward, inverse) = await LoadPairOrThrowAsync(tenantId, id, ct);

        var window = maxAge ?? TimeSpan.FromHours(24);
        if (DateTime.UtcNow - forward.CreatedDate > window)
            throw new FamilyRelationshipValidationException(
                $"Soft-delete is only permitted within {window.TotalHours:F0}h of creation. " +
                "Use POST /{id}/end to wind the relationship down instead.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new FamilyRelationshipValidationException("deletedReason is required.");

        var now = DateTime.UtcNow;
        foreach (var row in new[] { forward, inverse })
        {
            row.DeletedAt = now;
            row.DeletedBy = actor;
            row.DeletedReason = reason;
            StampUpdate(row, actor);
        }
        await _repo.UpdatePairAsync(forward, inverse, ct);
        return forward;
    }

    public Task<FamilyRelationship?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default) =>
        _repo.GetByIdAsync(tenantId, id, ct);

    public Task<List<FamilyRelationship>> ListForMemberAsync(
        string tenantId, string memberId, bool includeDeleted = false, CancellationToken ct = default) =>
        _repo.ListTouchingAsync(tenantId, memberId, includeDeleted, ct);

    public async Task<string?> DeriveSubscriberMemberIdAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        var edges = await _repo.ListBySubjectAsync(tenantId, memberId, includeDeleted: false, ct);
        var now = DateTime.UtcNow;

        // An active edge where the subject (memberId) stands in a "dependent-like"
        // role toward the counterparty. Picks the earliest-start relationship to
        // keep derivation stable across reads.
        var candidate = edges
            .Where(e => e.EndDate == null || e.EndDate > now)
            .Where(e => DependentToSubscriberCodes.Contains(e.RelationshipCode))
            .OrderBy(e => e.StartDate)
            .FirstOrDefault();

        return candidate?.RelatedMemberId;
    }

    private async Task<(FamilyRelationship Forward, FamilyRelationship Inverse)> LoadPairOrThrowAsync(
        string tenantId, string id, CancellationToken ct)
    {
        var anchor = await _repo.GetByIdAsync(tenantId, id, ct)
            ?? throw new FamilyRelationshipValidationException($"Relationship '{id}' not found.");
        var rows = await _repo.GetPairAsync(tenantId, anchor.PairId, ct);
        if (rows.Count != 2)
            throw new InvalidOperationException(
                $"Pair {anchor.PairId} has {rows.Count} rows — expected 2. Graph invariant violated.");

        var forward = rows.FirstOrDefault(r => r.Id == anchor.Id) ?? rows[0];
        var inverse = rows.FirstOrDefault(r => r.Id != anchor.Id) ?? rows[1];
        return (forward, inverse);
    }

    private static void StampUpdate(FamilyRelationship row, string? actor)
    {
        row.UpdatedDate = DateTime.UtcNow;
        row.UpdatedBy = actor;
    }
}
