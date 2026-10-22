using ProviderService.Models;

namespace ProviderService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
/// <summary>
/// Stub <see cref="IOrganizationAdapter"/> for tenants whose network
/// directory lives in TriZetto Facets. Every method throws
/// <see cref="NotImplementedException"/> with a clear migration TODO
/// until the payer-specific Facets connector is implemented and accepted.
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
