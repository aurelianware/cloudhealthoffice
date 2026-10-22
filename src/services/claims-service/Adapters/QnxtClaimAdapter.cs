using ClaimsService.Models;

namespace ClaimsService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
/// <summary>
/// Stub adapter for tenants whose claims live in QNXT (TriZetto / Cognizant).
/// All methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the payer-specific QNXT connector is implemented and accepted.
/// </summary>
/// <remarks>
/// TODO(qnxt-claims): integrate with the QNXT claim transaction API
/// (CLAIM_INQ on the QNXT claims stack). Reference doc:
/// docs/architecture/claim-adapter-pattern.md.
/// </remarks>
public class QnxtClaimAdapter : IClaimAdapter
{
    private const string Todo =
        "QNXT claim adapter not yet implemented. " +
        "TODO(qnxt-claims): integrate with the QNXT claim transaction API. " +
        "See docs/architecture/claim-adapter-pattern.md.";

    public string Platform => "qnxt";

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
