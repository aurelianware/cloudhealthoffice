using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberService.Models;

/// <summary>
/// Symmetric family-relationship edge. Every logical relationship is persisted as
/// two rows (A→B and B→A) written atomically so the graph is always symmetric.
///
/// Replaces <see cref="Member.SubscriberMemberId"/> as the source of truth for
/// subscriber/dependent structure. Legacy <c>SubscriberMemberId</c> is derived
/// from this graph on read for back-compat (see migration runbook at
/// <c>docs/migrations/family-relationships-backfill.md</c>).
///
/// Soft-delete only: use <c>EndDate</c> for normal wind-down; <c>DeletedAt</c>
/// for rare data-entry error corrections. Hard delete is not permitted — audit,
/// claims, authorizations and QMCSO references depend on historical rows
/// remaining retrievable.
/// </summary>
[BsonIgnoreExtraElements]
public class FamilyRelationship
{
    /// <summary>Cosmos document id / Mongo _id.</summary>
    [Required]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Partition key. Both sides of a symmetric pair share the same tenant, so the two
    /// rows live in the same Cosmos partition and can be written in a TransactionalBatch.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The member this edge belongs to (the "from" side).</summary>
    [Required]
    [StringLength(50)]
    public string SubjectMemberId { get; set; } = string.Empty;

    /// <summary>The member at the other end of the edge (the "to" side).</summary>
    [Required]
    [StringLength(50)]
    public string RelatedMemberId { get; set; } = string.Empty;

    /// <summary>
    /// X12 INS02 code describing how <see cref="RelatedMemberId"/> relates to
    /// <see cref="SubjectMemberId"/> (e.g., "19" when subject's child is related).
    /// See <see cref="RelationshipCodes"/>.
    /// </summary>
    [Required]
    [StringLength(4)]
    public string RelationshipCode { get; set; } = string.Empty;

    /// <summary>
    /// Transaction id that links the two symmetric rows (A→B and B→A) written together.
    /// Used to find the counterpart when ending, editing, or deleting.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string PairId { get; set; } = string.Empty;

    [Required]
    public DateTime StartDate { get; set; }

    /// <summary>Null for active relationships. Setting this is the normal wind-down path.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>True when the relationship carries custodial responsibility (minors, QMCSO).</summary>
    public bool IsCustodial { get; set; }

    /// <summary>QMCSO case reference for court-ordered medical coverage of minors.</summary>
    [StringLength(128)]
    public string? QmcsoReference { get; set; }

    [Required]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    public DateTime? UpdatedDate { get; set; }

    [StringLength(200)]
    public string? UpdatedBy { get; set; }

    // ── Soft-delete (rare; admin-only; reserved for data-entry errors) ───

    public DateTime? DeletedAt { get; set; }

    [StringLength(200)]
    public string? DeletedBy { get; set; }

    [StringLength(500)]
    public string? DeletedReason { get; set; }

    /// <summary>True when this row has been soft-deleted. Repositories filter it out by default.</summary>
    [BsonIgnore]
    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>True when this row is currently active (no EndDate, not soft-deleted).</summary>
    [BsonIgnore]
    public bool IsActive => !DeletedAt.HasValue && (EndDate == null || EndDate > DateTime.UtcNow);
}

/// <summary>
/// Known-good X12 INS02 relationship codes and inverse-relationship table.
///
/// Validator exists at the service boundary (see <c>FamilyRelationshipService</c>).
/// Unknown codes from imports are stored as-is on legacy <c>Member.RelationshipCode</c>;
/// the validator only runs on portal/API writes.
/// </summary>
public static class FamilyRelationshipCodes
{
    /// <summary>
    /// Inverse-relationship map. Child → Parent, Parent → Child, etc. Codes that are
    /// self-inverse (Spouse ↔ Spouse, Life Partner ↔ Life Partner) map to themselves.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Inverses = new Dictionary<string, string>
    {
        ["18"] = "18",   // Self ↔ Self (only valid when subject == related — rejected by validator)
        ["01"] = "01",   // Spouse ↔ Spouse
        ["19"] = "G8",   // Child → Parent
        ["G8"] = "19",   // Parent → Child (INS02 "Other" repurposed as parent — see note)
        ["53"] = "53",   // Life Partner ↔ Life Partner
        ["17"] = "G8",   // Stepchild → Stepparent
        ["10"] = "G8",   // Foster child → Foster parent
        ["20"] = "20",   // Employee ↔ Employer-sponsored (rare)
        ["22"] = "22",   // Handicapped dependent → guardian (self-inverse for symmetry)
        ["29"] = "29",   // Significant other
    };

    /// <summary>Full set of codes accepted at the API boundary.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(Inverses.Keys);

    public static bool IsValid(string? code) =>
        !string.IsNullOrWhiteSpace(code) && Known.Contains(code);

    /// <summary>
    /// Returns the inverse code for the B→A row of a symmetric pair, or <c>null</c>
    /// if <paramref name="code"/> is unknown. Writers must reject unknown codes at
    /// the service boundary — we do not invent inverses.
    /// </summary>
    public static string? Invert(string code) =>
        Inverses.TryGetValue(code, out var inverse) ? inverse : null;
}
