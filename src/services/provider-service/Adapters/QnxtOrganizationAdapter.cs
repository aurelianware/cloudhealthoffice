using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Stub <see cref="IOrganizationAdapter"/> for tenants whose network
/// directory lives in QNXT (TriZetto / Cognizant). Every method throws
/// <see cref="NotImplementedException"/> with a clear migration TODO
/// until the QNXT integration ships.
/// </summary>
/// <remarks>
/// TODO(qnxt-organization): integrate with the QNXT network configuration
/// surface (NETWORK_INQ on the QNXT network stack). Reference doc:
/// docs/architecture/network-as-organization.md.
/// </remarks>
public class QnxtOrganizationAdapter : IOrganizationAdapter
{
    private const string Todo =
        "QNXT organization adapter not yet implemented. " +
        "TODO(qnxt-organization): integrate with the QNXT network configuration API. " +
        "See docs/architecture/network-as-organization.md.";

    public string Platform => "qnxt";

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
