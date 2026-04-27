using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Tenant-routed abstraction over the network <see cref="Organization"/>
/// surface. Mirrors <see cref="IProviderAdapter"/>: each tenant is mapped
/// (via <see cref="ProviderTenantConfigCache"/>) to one adapter
/// implementation and the factory selects it case-insensitively by the
/// <see cref="Platform"/> property.
///
/// <para>
/// The DTO shape (<see cref="AdapterOrganization"/>) is FHIR-aligned so a
/// later projection layer (capabilities 5.5 / 5.7+) can render the same
/// object as a FHIR <c>Organization</c> resource without translation.
/// </para>
/// </summary>
public interface IOrganizationAdapter
{
    /// <summary>Platform identifier matching tenant config (e.g. <c>cho</c>, <c>qnxt</c>).</summary>
    string Platform { get; }

    /// <summary>Fetch a network by chain key (or by VersionId when supplied).</summary>
    Task<OrganizationAdapterResponse> GetOrganizationAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default);

    /// <summary>Children of a parent network for partOf hierarchy traversal.</summary>
    Task<OrganizationListAdapterResponse> GetByParentAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Paginated list filtered by <see cref="OrganizationAdapterRequest.NetworkType"/>
    /// and/or <see cref="OrganizationAdapterRequest.LineOfBusiness"/>.
    /// </summary>
    Task<OrganizationListAdapterResponse> ListAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default);
}
