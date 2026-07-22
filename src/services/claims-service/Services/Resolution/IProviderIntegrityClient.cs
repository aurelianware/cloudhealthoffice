namespace ClaimsService.Services.Resolution;

/// <summary>
/// Client for benefit-plan-service's <c>GET /api/v1/adjudication/provider-integrity/{npi}</c>
/// endpoint -- the same <c>HttpProviderIntegrityGate</c> federal-exclusion
/// check <c>AdjudicationController.Adjudicate</c> runs internally, exposed
/// standalone so claims-service's <c>ProviderIntegrityStage</c> can run it
/// without going through <c>calculate-benefits</c> (which stays
/// exclusion-check-free by design). See
/// <c>docs/architecture/integrity-score-consumption.md</c>.
/// </summary>
public interface IProviderIntegrityClient
{
    /// <summary>
    /// Resolve the integrity result for <paramref name="npi"/>.
    /// Returns <c>null</c> whenever no result could be obtained from
    /// benefit-plan-service -- a transport failure reaching it, a
    /// non-success HTTP status, an empty/whitespace <paramref name="npi"/>,
    /// or a response body that failed to deserialize. Callers must treat
    /// <c>null</c> as "could not verify" and hold the claim for review
    /// (see <c>ProviderIntegrityStage</c>), the same as any other
    /// inconclusive result -- it is never safe to treat as a pass. A
    /// non-null result, in turn, is always a confident answer: the gate's
    /// own "never fail open" contract already covers failures reaching
    /// its own upstreams (provider-service / provider-verification-service)
    /// by returning <see cref="ProviderIntegritySnapshot.RequiresManualReview"/>
    /// rather than a fail-open pass.
    /// </summary>
    Task<ProviderIntegritySnapshot?> CheckAsync(
        string tenantId,
        string npi,
        CancellationToken ct = default);
}

/// <summary>
/// Claims-service-side mirror of benefit-plan-service's
/// <c>ProviderIntegrityResult</c>.
/// </summary>
public sealed record ProviderIntegritySnapshot
{
    public bool Passed { get; init; }
    public bool IsExcluded { get; init; }
    public bool RequiresManualReview { get; init; }
    public string? DenialCode { get; init; }
    public string? DenialReason { get; init; }
    public string? Rating { get; init; }
}
