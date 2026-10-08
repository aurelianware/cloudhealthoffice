using CloudHealthOffice.FeeScheduleEngine.Models;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// Client for benefit-plan-service's <c>POST /api/v1/adjudication/resolve-rates</c>
/// endpoint — the fee schedule engine's
/// <c>IRateResolutionService.ResolveBatchAsync</c>, the same resolution the
/// synchronous <c>AdjudicationController.Adjudicate</c> path runs in-process.
/// Consumed by <c>PricingStage</c> (Order=250) so the async adjudication
/// pipeline prices lines with the identical contract → fee schedule →
/// modifier → multiple-procedure logic instead of letting the benefit
/// engine fall back to allowed = billed.
/// </summary>
public interface IFeeSchedulePricingClient
{
    /// <summary>
    /// Price every line in <paramref name="requests"/> as one batch (batch
    /// order drives multiple-procedure ranking). Returns <c>null</c> when no
    /// answer could be obtained — transport failure, timeout, non-success
    /// HTTP status, or an unreadable body. Callers must treat <c>null</c> as
    /// "could not price" and hold the claim; it is never safe to treat as
    /// allowed = billed.
    /// </summary>
    Task<PricingResultSet?> ResolveBatchAsync(
        string tenantId,
        IReadOnlyList<PricingRequest> requests,
        CancellationToken ct = default);
}
