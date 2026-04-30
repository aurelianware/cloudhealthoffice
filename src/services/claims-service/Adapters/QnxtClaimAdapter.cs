using ClaimsService.Models;

namespace ClaimsService.Adapters;

/// <summary>
/// Stub adapter for tenants whose claims live in QNXT (TriZetto / Cognizant).
/// All methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the QNXT integration ships.
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
