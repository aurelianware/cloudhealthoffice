using System;
using System.ComponentModel.DataAnnotations;

namespace ProviderService.Models;

/// <summary>
/// Payer-defined provider network represented as a first-class entity.
///
/// <para>
/// Distinct from a <see cref="Provider"/> with <c>ProviderType=Organization</c>
/// (which represents a single facility — hospital, clinic, group practice).
/// An <see cref="Organization"/> here is the network construct referenced
/// by claims, benefit plans (network tiers — capability 5.5), and FHIR
/// <c>Organization</c> projections. The shape is deliberately FHIR-aligned:
/// <c>Identifiers</c>, <c>Name</c>, <c>PartOf</c> (via <see cref="ParentOrganizationId"/>),
/// effective <c>Period</c>, and <c>Contact</c> all map cleanly onto
/// FHIR R4 <c>Organization</c>.
/// </para>
///
/// <para>
/// Each row is one immutable version. The chain key is
/// <see cref="OrganizationId"/> (preserved across amendments); each
/// per-version row carries its own <see cref="Id"/> + <see cref="VersionId"/>,
/// matching the provider versioning model from capability 5.1.
/// </para>
/// </summary>
public class Organization
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation).
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Per-version document id (Cosmos DB document id / Mongo _id).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Stable chain key — the persistent network identifier preserved
    /// across every version row. For genesis rows <c>OrganizationId == Id</c>;
    /// amend versions share the same <c>OrganizationId</c> with a new per-row
    /// <c>Id</c>. Empty on the wire is the legacy marker; hydration sets
    /// <c>OrganizationId = Id</c>.
    /// </summary>
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable network name (e.g. "Aetna Open Access HMO Florida 2025").
    /// </summary>
    [Required]
    [StringLength(300)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Payer-defined network classification. <see cref="NetworkType.Unknown"/>
    /// is the safe default for documents written before this field existed.
    /// </summary>
    [Required]
    public NetworkType NetworkType { get; set; } = NetworkType.Unknown;

    /// <summary>
    /// Line of business this network applies to. Preserved from
    /// <c>Provider.NetworkParticipation.LineOfBusiness</c> so existing
    /// search filters and policy checks continue to read the same field
    /// shape on the network entity.
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Optional parent network for partOf hierarchies (e.g. a sub-network
    /// nested inside a parent group). Maps to FHIR <c>Organization.partOf</c>.
    /// Null on top-level networks.
    /// </summary>
    [StringLength(64)]
    public string? ParentOrganizationId { get; set; }

    /// <summary>
    /// External / payer-system identifiers (e.g. trading-partner network
    /// id, contract id, regulatory filing id). Maps to FHIR
    /// <c>Organization.identifier</c>.
    /// </summary>
    public List<OrganizationIdentifier> Identifiers { get; set; } = new();

    /// <summary>
    /// Effective date of the network (inclusive). Required.
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Termination date (exclusive). Null while the network is open-ended.
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Free-text reason captured when the head version is terminated.
    /// </summary>
    [StringLength(500)]
    public string? TerminationReason { get; set; }

    /// <summary>
    /// Network operational status — distinct from
    /// <see cref="VersionState"/>, which describes lifecycle of the row.
    /// </summary>
    [Required]
    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;

    /// <summary>
    /// Network contact information (admin contact, support phone, address).
    /// Maps to FHIR <c>Organization.contact</c> + <c>Organization.address</c>.
    /// </summary>
    public OrganizationContactInfo? ContactInfo { get; set; }

    // ---------------------------------------------------------------------
    // Audit fields
    // ---------------------------------------------------------------------

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }

    // ---------------------------------------------------------------------
    // Version-chain identity (mirrors capability 5.1)
    //
    // Every row in the Organizations collection is one immutable version.
    // The chain is keyed on (TenantId, OrganizationId); VersionId is the
    // ULID per-row key. Documents written before these fields existed
    // hydrate as VersionState=Active, VersionNumber=1, OrganizationId=Id.
    // ---------------------------------------------------------------------

    public string VersionId { get; set; } = string.Empty;

    public int VersionNumber { get; set; }

    public OrganizationVersionState VersionState { get; set; }

    public string? PredecessorVersionId { get; set; }

    public DateTime? ActivatedAt { get; set; }

    [StringLength(200)]
    public string? ActivatedBy { get; set; }

    public DateTime? SuspendedAt { get; set; }

    [StringLength(500)]
    public string? SuspensionReason { get; set; }

    public DateTime? SupersededAt { get; set; }

    [StringLength(64)]
    public string? SupersededByVersionId { get; set; }
}

/// <summary>
/// External identifier on an <see cref="Organization"/>. Shape mirrors
/// FHIR <c>Identifier</c> so projections in capability 5.5 / 5.7+ can
/// pass the value through without translation.
/// </summary>
public class OrganizationIdentifier
{
    /// <summary>Issuing system URI (e.g. <c>urn:cho:network</c>, <c>http://hl7.org/fhir/sid/us-npi</c>).</summary>
    [Required]
    [StringLength(200)]
    public string System { get; set; } = string.Empty;

    /// <summary>Identifier value within the issuing system.</summary>
    [Required]
    [StringLength(200)]
    public string Value { get; set; } = string.Empty;

    /// <summary>Identifier type code (e.g. <c>NIIP</c>, <c>TAX</c>, <c>PRN</c>).</summary>
    [StringLength(50)]
    public string? Type { get; set; }

    /// <summary>FHIR identifier-use category (<c>usual</c>, <c>official</c>, <c>secondary</c>, ...).</summary>
    [StringLength(20)]
    public string? Use { get; set; }
}

/// <summary>
/// Contact information on an <see cref="Organization"/>. Kept distinct
/// from <c>tenant-service</c>'s <c>ContactInfo</c> so the two services
/// can evolve their shapes independently.
/// </summary>
public class OrganizationContactInfo
{
    [StringLength(200)]
    public string? PrimaryContactName { get; set; }

    [Phone]
    [StringLength(20)]
    public string? Phone { get; set; }

    [Phone]
    [StringLength(20)]
    public string? Fax { get; set; }

    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; set; }

    [StringLength(300)]
    public string? Address { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(2)]
    public string? State { get; set; }

    [StringLength(10)]
    public string? ZipCode { get; set; }

    [StringLength(500)]
    public string? Website { get; set; }
}

/// <summary>
/// Operational status of an <see cref="Organization"/> network, separate
/// from row lifecycle (<see cref="OrganizationVersionState"/>).
///
/// <para>String-only / no-integer enforcement is delegated to the shared
/// MVC JSON options registered by <c>AddCloudHealthOfficeJsonOptions</c>.</para>
/// </summary>
public enum OrganizationStatus
{
    Unknown = 0,
    Active = 1,
    Inactive = 2,
    Terminated = 3,
    Pending = 4
}
