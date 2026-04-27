namespace ProviderService.Models;

/// <summary>
/// Vendor-neutral request envelope for any <see cref="Adapters.IOrganizationAdapter"/>.
/// Mirrors <see cref="ProviderAdapterRequest"/>; per-method required fields
/// are documented on each property.
/// </summary>
public class OrganizationAdapterRequest
{
    /// <summary>Tenant id resolved by the request middleware. Required by all methods.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Network chain key (<see cref="Organization.OrganizationId"/>). Required by
    /// <c>GetOrganizationAsync</c>; ignored otherwise.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Specific version identifier (ULID). Optional on <c>GetOrganizationAsync</c>
    /// — when set, adapters return that exact version instead of the latest
    /// non-Draft head.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>
    /// Parent network identifier — used by <c>GetByParentAsync</c> to walk
    /// the partOf hierarchy. Required by <c>GetByParentAsync</c>; ignored otherwise.
    /// </summary>
    public string? ParentOrganizationId { get; set; }

    /// <summary>Optional <see cref="NetworkType"/> filter for list queries.</summary>
    public NetworkType? NetworkType { get; set; }

    /// <summary>Optional line-of-business filter for list queries.</summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// Effective date for service-date queries. Optional; when null,
    /// adapters should treat it as <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public DateTime? ServiceDate { get; set; }

    /// <summary>1-based page index for paged list results.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size for paged list results.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Platform-specific configuration sourced from tenant config
    /// (e.g. QNXT base URL). Adapters read what they need.
    /// </summary>
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}
