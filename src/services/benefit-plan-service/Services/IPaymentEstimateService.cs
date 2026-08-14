using BenefitPlanService.Models.Estimate;

namespace BenefitPlanService.Services;

/// <summary>
/// Produces a read-only prospective claim payment estimate by reusing the
/// existing CHO adjudication engines (fee-schedule pricing + benefit
/// calculation) in a side-effect-free simulation mode.
///
/// <para>
/// Implementations MUST NOT mutate any persistent financial state: no claim,
/// payment record, claim history, accumulator, visit/frequency counter,
/// remittance, downstream workflow, or authorization state is created or
/// changed by producing an estimate.
/// </para>
/// </summary>
public interface IPaymentEstimateService
{
    /// <summary>
    /// Compute a prospective payment estimate for <paramref name="request"/>
    /// within the given <paramref name="tenantId"/>.
    /// </summary>
    /// <param name="tenantId">
    /// Authenticated tenant context. The estimate is always scoped to this
    /// tenant; any tenant id present in the request body is ignored.
    /// </param>
    Task<PaymentEstimateResponse> EstimateAsync(
        string tenantId,
        PaymentEstimateRequest request,
        CancellationToken ct = default);
}
