using BenefitPlanService.Models;

namespace BenefitPlanService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
/// <summary>
/// Stub adapter for tenants whose benefit plans live in TriZetto Facets.
/// All methods throw <see cref="NotImplementedException"/> with a clear
/// migration TODO until the payer-specific Facets connector is implemented and accepted.
/// </summary>
/// <remarks>
/// TODO(facets-benefit-plan): integrate with the Facets benefit/product
/// inquiry surface (typically the Open Access XML interface or
/// Facets Workflow service). Reference doc:
/// docs/architecture/benefit-plan-adapter-pattern.md.
/// </remarks>
public class FacetsBenefitPlanAdapter : IBenefitPlanAdapter
{
    private const string Todo =
        "Facets benefit plan adapter not yet implemented. " +
        "TODO(facets-benefit-plan): integrate with the Facets benefit/product inquiry interface. " +
        "See docs/architecture/benefit-plan-adapter-pattern.md.";

    public string Platform => "facets";

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
