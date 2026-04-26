using BenefitPlanService.Models;

namespace BenefitPlanService.Adapters;

/// <summary>
/// Stub adapter for tenants whose benefit plans live in HealthEdge HealthRules
/// Payer. All methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the HealthEdge integration ships.
/// </summary>
/// <remarks>
/// TODO(healthedge-benefit-plan): integrate with the HealthRules Payer plan
/// inquiry API (HRP REST surface). Reference doc:
/// docs/architecture/benefit-plan-adapter-pattern.md.
/// </remarks>
public class HealthEdgeBenefitPlanAdapter : IBenefitPlanAdapter
{
    private const string Todo =
        "HealthEdge benefit plan adapter not yet implemented. " +
        "TODO(healthedge-benefit-plan): integrate with the HealthRules Payer plan inquiry API. " +
        "See docs/architecture/benefit-plan-adapter-pattern.md.";

    public string Platform => "healthedge";

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
