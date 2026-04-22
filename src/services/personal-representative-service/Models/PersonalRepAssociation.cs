using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace PersonalRepresentativeService.Models;

/// <summary>
/// Edge linking a <see cref="PersonalRepresentative"/> to a member. Persisted
/// as a symmetric pair: one row with <see cref="Direction"/> =
/// <see cref="AssociationDirection.RepToMember"/> and a counterpart row with
/// <see cref="AssociationDirection.MemberToRep"/>, sharing a <see cref="PairId"/>.
/// Both rows are written atomically (Cosmos <c>TransactionalBatch</c> or
/// Mongo session transaction) so "list reps for this member" and "list
/// members for this rep" queries are always consistent.
///
/// Fields mean the same thing on both rows: <see cref="RepId"/> is always
/// the rep id; <see cref="MemberId"/> is always the member id. The
/// <see cref="Direction"/> discriminator says which lookup this row is
/// indexed for, not which end of the edge is "subject."
///
/// Soft-delete only (<see cref="EffectiveTo"/> for normal wind-down,
/// <see cref="DeletedAt"/> for rare data-entry error correction) — matches
/// the FamilyRelationship lifecycle posture.
/// </summary>
[BsonIgnoreExtraElements]
public class PersonalRepAssociation
{
    [Required]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Partition key. Both rows of a pair share the same tenant.</summary>
    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Links the two symmetric rows of an association. Used to find the
    /// counterpart on end / soft-delete operations.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string PairId { get; set; } = string.Empty;

    /// <summary>The Personal Representative id. Same value on both rows.</summary>
    [Required]
    [StringLength(50)]
    public string RepId { get; set; } = string.Empty;

    /// <summary>The external member id. Same value on both rows.</summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Which lookup this row is indexed for. The pair's two rows share every
    /// field except this discriminator.
    /// </summary>
    [Required]
    public AssociationDirection Direction { get; set; }

    /// <summary>
    /// Credential copied from the parent <see cref="PersonalRepresentative"/>
    /// for query-path ergonomics (avoids a join for the resolver endpoint).
    /// </summary>
    [Required]
    public PersonalRepCredentialType CredentialType { get; set; }

    [Required]
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Null for active associations. Setting this is the normal wind-down path.</summary>
    public DateTime? EffectiveTo { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [StringLength(200)]
    public string? UpdatedBy { get; set; }

    // ── Soft-delete (rare; admin-only; reserved for data-entry errors) ───

    public DateTime? DeletedAt { get; set; }

    [StringLength(200)]
    public string? DeletedBy { get; set; }

    [StringLength(500)]
    public string? DeletedReason { get; set; }

    [BsonIgnore]
    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>True when this row is currently active (no EndDate passed, not soft-deleted).</summary>
    [BsonIgnore]
    public bool IsActive => !DeletedAt.HasValue && (EffectiveTo == null || EffectiveTo > DateTime.UtcNow);
}

/// <summary>
/// Which side of the symmetric pair this row is indexed for. The fields on
/// both rows have identical values; this discriminator simply says whether
/// a query should hit this row when looking up "reps for a member" or
/// "members for a rep."
/// </summary>
public enum AssociationDirection
{
    /// <summary>Indexed for "list all members for this rep" queries.</summary>
    RepToMember = 1,

    /// <summary>Indexed for "list all reps for this member" queries.</summary>
    MemberToRep = 2
}
