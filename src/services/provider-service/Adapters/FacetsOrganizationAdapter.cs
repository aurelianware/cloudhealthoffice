using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Stub <see cref="IOrganizationAdapter"/> for tenants whose network
/// directory lives in TriZetto Facets. Every method throws
/// <see cref="NotImplementedException"/> with a clear migration TODO
/// until the Facets integration ships.
/// </summary>
/// <remarks>
/// TODO(facets-organization): integrate with the Facets network setup
/// interface (typically the Facets Open Access XML or Workflow service).
/// Reference doc: docs/architecture/network-as-organization.md.
/// </remarks>
public class FacetsOrganizationAdapter : IOrganizationAdapter
{
    private const string Todo =
        "Facets organization adapter not yet implemented. " +
        "TODO(facets-organization): integrate with the Facets network setup interface. " +
        "See docs/architecture/network-as-organization.md.";

    public string Platform => "facets";

    public Task<OrganizationAdapterResponse> GetOrganizationAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<OrganizationListAdapterResponse> GetByParentAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<OrganizationListAdapterResponse> ListAsync(
        OrganizationAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);
}
