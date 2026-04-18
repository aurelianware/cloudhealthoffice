using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Models;

namespace MemberService.Services;

/// <summary>
/// Service-layer API for the family-relationship graph. Enforces symmetric writes,
/// validates relationship codes at the trust boundary, guards same-tenant
/// constraint, and runs soft-delete semantics.
/// </summary>
public interface IFamilyRelationshipService
{
    /// <summary>
    /// Create a symmetric relationship between <paramref name="request.SubjectMemberId"/>
    /// and <paramref name="request.RelatedMemberId"/>. Returns the forward row; caller can
    /// fetch the pair via <see cref="GetPairAsync"/> if needed.
    /// </summary>
    Task<FamilyRelationship> CreateAsync(string tenantId, CreateFamilyRelationshipRequest request, string? actor, CancellationToken ct = default);

    /// <summary>Edit non-identity fields (dates, custodial flag, QMCSO reference) on a pair.</summary>
    Task<FamilyRelationship> UpdateAsync(string tenantId, string id, UpdateFamilyRelationshipRequest request, string? actor, CancellationToken ct = default);

    /// <summary>End-date a relationship (normal wind-down). Updates both rows of the pair.</summary>
    Task<FamilyRelationship> EndAsync(string tenantId, string id, DateTime endDate, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Soft-delete (data-entry error correction). Row must have been created within
    /// <paramref name="maxAge"/> (default: 24h). Never purges. Authorization of the
    /// acting user is the caller's responsibility — enforce it at the controller /
    /// API boundary, not here.
    /// </summary>
    Task<FamilyRelationship> SoftDeleteAsync(string tenantId, string id, string reason, string? actor, TimeSpan? maxAge = null, CancellationToken ct = default);

    Task<FamilyRelationship?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>All relationships touching a member, grouped for the portal Family tab.</summary>
    Task<List<FamilyRelationship>> ListForMemberAsync(string tenantId, string memberId, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>
    /// Derive the legacy <see cref="Member.SubscriberMemberId"/> value for a dependent
    /// from the active relationship graph. Returns null when no subscriber relationship
    /// (code "19"/"17"/"10" — dependent → subscriber) exists. Used by read paths to
    /// keep the obsolete field populated for back-compat.
    /// </summary>
    Task<string?> DeriveSubscriberMemberIdAsync(string tenantId, string memberId, CancellationToken ct = default);
}

public class CreateFamilyRelationshipRequest
{
    public string SubjectMemberId { get; set; } = string.Empty;
    public string RelatedMemberId { get; set; } = string.Empty;
    /// <summary>X12 INS02 code. Validated against <see cref="FamilyRelationshipCodes"/>.</summary>
    public string RelationshipCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
}

public class UpdateFamilyRelationshipRequest
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool? IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
}

/// <summary>
/// Thrown when a relationship write violates an invariant (symmetric-graph, same-tenant,
/// invalid code, member-not-found). Mapped to 400 in the controller. See
/// <see cref="DuplicateFamilyRelationshipException"/> for the duplicate-active-pair case
/// — callers that need to no-op on re-runs (the shim, the backfill) catch that subtype
/// rather than inspecting error-message text.
/// </summary>
public class FamilyRelationshipValidationException : Exception
{
    public FamilyRelationshipValidationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when a create would produce a second active pair between two members. Distinct
/// type so idempotent callers (shim / backfill) can reliably detect it without
/// message-matching.
/// </summary>
public class DuplicateFamilyRelationshipException : FamilyRelationshipValidationException
{
    public DuplicateFamilyRelationshipException(string message) : base(message) { }
}
