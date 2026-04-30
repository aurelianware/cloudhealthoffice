using ClaimsService.Models;

namespace ClaimsService.Adapters;

/// <summary>
/// Abstraction for claim platforms. Each tenant can be configured to use a
/// different adapter (CHO, QNXT, Facets, HealthEdge, ...). The adapter
/// normalizes platform-specific responses into a common, vendor-neutral
/// format designed to project cleanly onto a future FHIR <c>Claim</c> /
/// <c>ClaimResponse</c> resource (capability 5.11).
/// </summary>
/// <remarks>
/// Mirrors <c>ProviderService.Adapters.IProviderAdapter</c> and
/// <c>BenefitPlanService.Adapters.IBenefitPlanAdapter</c>. The selection
/// mechanism (factory consults tenant-service config and falls back to
/// "cho") is identical.
///
/// <para>
/// The interface is read-mostly: <c>SubmitClaimAsync</c> is the only write
/// path and just delegates to the repository's existing <c>CreateAsync</c>.
/// Adjudication writes (<c>UpdateAdjudicationProjectionAsync</c>) and
/// version-event emission (<c>IClaimVersionEventPublisher</c>) stay on the
/// CHO-internal repository / publisher seams — vendor systems own their own
/// adjudication state and audit chains, so those surfaces are deliberately
/// not on the adapter boundary.
/// </para>
/// </remarks>
public interface IClaimAdapter
{
    /// <summary>
    /// Platform identifier matching <c>configuration.claimsPlatform.platform</c>
    /// on the tenant. Resolution by the factory is case-insensitive.
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// Fetch a single claim. When <see cref="ClaimAdapterRequest.ClaimVersionId"/>
    /// is set the CHO adapter resolves the latest non-Draft version of the
    /// chain in effect at <see cref="ClaimAdapterRequest.AsOf"/>; otherwise
    /// it looks up the per-version document by <see cref="ClaimAdapterRequest.ClaimId"/>.
    /// Returns a response with <c>Claim == null</c> when not found.
    /// </summary>
    Task<ClaimAdapterResponse> GetClaimAsync(
        ClaimAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Fetch a single claim by payer-assigned claim number
    /// (<see cref="ClaimAdapterRequest.ClaimNumber"/>). Returns a response
    /// with <c>Claim == null</c> when not found.
    /// </summary>
    Task<ClaimAdapterResponse> GetClaimByNumberAsync(
        ClaimAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Fetch a specific version row identified by
    /// <see cref="ClaimAdapterRequest.ClaimVersionId"/> +
    /// <see cref="ClaimAdapterRequest.VersionId"/>. Returns a response with
    /// <c>Claim == null</c> when the version is not found.
    /// </summary>
    Task<ClaimAdapterResponse> GetClaimVersionAsync(
        ClaimAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// List versions of a claim chain newest-first, paginated with a
    /// continuation token. Vendor adapters surface their own version
    /// histories the same way so capability 5.11 (FHIR <c>Bundle</c> of all
    /// <c>ClaimResponse</c> versions) and 5.12 (Adjustment Workflow chain
    /// visualization) work uniformly across platforms.
    /// </summary>
    Task<ClaimVersionListAdapterResponse> ListClaimVersionsAsync(
        ClaimAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Submit a new claim. The CHO adapter delegates to
    /// <c>IClaimRepository.CreateAsync</c>, which already initializes the
    /// version chain (<c>ClaimVersionId=Id</c>, <c>VersionNumber=1</c>,
    /// <c>VersionState=Submitted</c>). Event emission is deferred to the
    /// submission service that wires this method (capability 5.3) — the
    /// adapter itself stays out of the lifecycle-event seam.
    /// </summary>
    Task<ClaimAdapterResponse> SubmitClaimAsync(
        ClaimSubmissionAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// General claim search with the canonical filter set
    /// (member, provider, service-date range, status, line of business),
    /// returning the requested page. Mirrors
    /// <c>IClaimRepository.SearchAsync</c>.
    /// </summary>
    Task<ClaimSearchAdapterResponse> SearchClaimsAsync(
        ClaimSearchAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Member-scoped claim search powering the portal Member Details
    /// dialog. Always requires <see cref="ClaimMemberSearchAdapterRequest.MemberId"/>
    /// and adds amount-range / claim-type filters on top of the canonical
    /// search set. Mirrors <c>IClaimRepository.SearchForMemberAsync</c>.
    /// </summary>
    Task<ClaimSearchAdapterResponse> SearchClaimsForMemberAsync(
        ClaimMemberSearchAdapterRequest request, CancellationToken ct = default);
}
