using BenefitPlanService.Models;

namespace BenefitPlanService.Adapters;

/// <summary>
/// Stub adapter for tenants whose benefit plans live in QNXT
/// (TriZetto / Cognizant). All methods throw <see cref="NotImplementedException"/>
/// with a clear migration TODO until the QNXT integration ships.
/// </summary>
/// <remarks>
/// TODO(qnxt-benefit-plan): integrate with the QNXT plan inquiry API
/// (BENEFIT_PLAN_INQ on the QNXT benefits stack). Reference doc:
/// docs/architecture/benefit-plan-adapter-pattern.md.
/// </remarks>
public class QnxtBenefitPlanAdapter : IBenefitPlanAdapter
{
    private const string Todo =
        "QNXT benefit plan adapter not yet implemented. " +
        "TODO(qnxt-benefit-plan): integrate with the QNXT plan inquiry API. " +
        "See docs/architecture/benefit-plan-adapter-pattern.md.";

    public string Platform => "qnxt";

    public Task<BenefitPlanAdapterResponse> GetPlanAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<BenefitPlanAdapterResponse> GetPlanVersionAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<MemberBenefitViewAdapterResponse> GetMemberBenefitViewAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);
}
