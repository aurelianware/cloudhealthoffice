using ClaimsService.Models;

namespace ClaimsService.Adapters;

/// <summary>
/// Stub adapter for tenants whose claims live in TriZetto Facets. All
/// methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the Facets integration ships.
/// </summary>
/// <remarks>
/// TODO(facets-claims): integrate with the Facets claim API (CLM transaction
/// set on the Facets claims stack). Reference doc:
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
