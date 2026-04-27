namespace ProviderService.Models;

/// <summary>
/// Vendor-neutral response envelope for
/// <see cref="Adapters.IOrganizationAdapter.GetOrganizationAsync"/>.
/// Shape mirrors <see cref="ProviderAdapterResponse"/> — a Platform tag,
/// optional raw audit blob, and a normalized <see cref="AdapterOrganization"/>.
/// </summary>
public class OrganizationAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Organization payload. Null when not found.</summary>
    public AdapterOrganization? Organization { get; set; }
}

/// <summary>
/// Vendor-neutral response envelope for collection-returning adapter
/// methods (<see cref="Adapters.IOrganizationAdapter.ListAsync"/> and
/// <see cref="Adapters.IOrganizationAdapter.GetByParentAsync"/>).
/// </summary>
public class OrganizationListAdapterResponse
{
    public string Platform { get; set; } = string.Empty;
    public string? RawResponse { get; set; }

    /// <summary>Page of organizations returned by the adapter (never null; may be empty).</summary>
    public IReadOnlyList<AdapterOrganization> Organizations { get; set; } = Array.Empty<AdapterOrganization>();

    /// <summary>Total matching count when the platform reports it; null otherwise.</summary>
    public int? TotalCount { get; set; }
}

/// <summary>
/// Normalized organization DTO. Field shape mirrors <see cref="Organization"/>
/// so the CHO pass-through is lossless and downstream FHIR projections
/// (capabilities 5.5, 5.7+) can consume the same record.
/// </summary>
public class AdapterOrganization
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public NetworkType NetworkType { get; set; }
    public LineOfBusiness LineOfBusiness { get; set; }
    public string? ParentOrganizationId { get; set; }

    public List<OrganizationIdentifier> Identifiers { get; set; } = new();

    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }

    public OrganizationStatus Status { get; set; }
    public OrganizationContactInfo? ContactInfo { get; set; }

    public DateTime CreatedDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public string? LastUpdatedBy { get; set; }

    // Version-chain identity
    public string VersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public OrganizationVersionState VersionState { get; set; }
    public string? PredecessorVersionId { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTime? SupersededAt { get; set; }
    public string? SupersededByVersionId { get; set; }

    public static AdapterOrganization From(Organization src) => new()
    {
        Id = src.Id,
        TenantId = src.TenantId,
        OrganizationId = src.OrganizationId,
        Name = src.Name,
        NetworkType = src.NetworkType,
        LineOfBusiness = src.LineOfBusiness,
        ParentOrganizationId = src.ParentOrganizationId,
        Identifiers = src.Identifiers.ToList(),
        EffectiveDate = src.EffectiveDate,
        TerminationDate = src.TerminationDate,
        TerminationReason = src.TerminationReason,
        Status = src.Status,
        ContactInfo = src.ContactInfo,
        CreatedDate = src.CreatedDate,
        LastUpdatedDate = src.LastUpdatedDate,
        CreatedBy = src.CreatedBy,
        LastUpdatedBy = src.LastUpdatedBy,
        VersionId = src.VersionId,
        VersionNumber = src.VersionNumber,
        VersionState = src.VersionState,
        PredecessorVersionId = src.PredecessorVersionId,
        ActivatedAt = src.ActivatedAt,
        ActivatedBy = src.ActivatedBy,
        SuspendedAt = src.SuspendedAt,
        SuspensionReason = src.SuspensionReason,
        SupersededAt = src.SupersededAt,
        SupersededByVersionId = src.SupersededByVersionId,
    };

    public Organization ToOrganization() => new()
    {
        Id = Id,
        TenantId = TenantId,
        OrganizationId = OrganizationId,
        Name = Name,
        NetworkType = NetworkType,
        LineOfBusiness = LineOfBusiness,
        ParentOrganizationId = ParentOrganizationId,
        Identifiers = Identifiers.ToList(),
        EffectiveDate = EffectiveDate,
        TerminationDate = TerminationDate,
        TerminationReason = TerminationReason,
        Status = Status,
        ContactInfo = ContactInfo,
        CreatedDate = CreatedDate,
        LastUpdatedDate = LastUpdatedDate,
        CreatedBy = CreatedBy,
        LastUpdatedBy = LastUpdatedBy,
        VersionId = VersionId,
        VersionNumber = VersionNumber,
        VersionState = VersionState,
        PredecessorVersionId = PredecessorVersionId,
        ActivatedAt = ActivatedAt,
        ActivatedBy = ActivatedBy,
        SuspendedAt = SuspendedAt,
        SuspensionReason = SuspensionReason,
        SupersededAt = SupersededAt,
        SupersededByVersionId = SupersededByVersionId,
    };
}
