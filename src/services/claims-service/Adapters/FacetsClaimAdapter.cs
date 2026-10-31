using ClaimsService.Models;

namespace ClaimsService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
/// <summary>
/// Stub adapter for tenants whose claims live in TriZetto Facets. All
/// methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the payer-specific Facets connector is implemented and accepted.
/// </summary>
/// <remarks>
/// TODO(facets-claims): integrate with the payer's licensed Facets interface
/// (operation names come from that specification, not from this comment).
/// Reference docs: docs/integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md,
/// docs/architecture/claim-adapter-pattern.md.
/// </remarks>
public class FacetsClaimAdapter : IClaimAdapter
{
    private const string Todo =
        "Facets claim adapter not yet implemented. " +
        "TODO(facets-claims): integrate with the Facets claim API. " +
        "See docs/architecture/claim-adapter-pattern.md.";

    public string Platform => "facets";

    public Task<ClaimAdapterResponse> GetClaimAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ClaimAdapterResponse> GetClaimByNumberAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ClaimAdapterResponse> GetClaimVersionAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ClaimVersionListAdapterResponse> ListClaimVersionsAsync(
        ClaimAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ClaimAdapterResponse> SubmitClaimAsync(
        ClaimSubmissionAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ClaimSearchAdapterResponse> SearchClaimsAsync(
        ClaimSearchAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<ClaimSearchAdapterResponse> SearchClaimsForMemberAsync(
        ClaimMemberSearchAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);
}
