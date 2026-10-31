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
/// directory lives in QNXT (TriZetto / Cognizant). Every method throws
/// <see cref="NotImplementedException"/> with a clear migration TODO
/// until the payer-specific QNXT connector is implemented and accepted.
/// </summary>
/// <remarks>
/// TODO(qnxt-organization): integrate with the payer's licensed QNXT interface
/// (operation names come from that specification, not from this comment).
/// Reference docs: docs/integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md,
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
