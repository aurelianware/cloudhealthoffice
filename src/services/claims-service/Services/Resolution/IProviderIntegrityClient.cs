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
    /// Returns <c>null</c> only on a transport failure reaching
    /// benefit-plan-service itself -- the gate's own "never fail open"
    /// contract already covers failures reaching its own upstreams
    /// (provider-service / provider-verification-service), so a non-null
    /// result here is always a confident answer, never a fail-open default.
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
