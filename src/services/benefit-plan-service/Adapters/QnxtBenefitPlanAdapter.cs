using BenefitPlanService.Models;

namespace BenefitPlanService.Adapters;

// Architecture intent: CHO is the reference backend. This is a separate,
// payer-specific connector template completed from QRE discovery (version,
// configuration, licensing, hosting and solution architecture). Its unimplemented
// methods are engagement work, not proof the native workflow is missing.
// See docs/adr/027-core-adapter-engagement-boundary.md and
// docs/features/questionnaires/qre-core-admin-adapter-questions.md.
/// <summary>
/// Stub adapter for tenants whose benefit plans live in QNXT
/// (TriZetto / Cognizant). All methods throw <see cref="NotImplementedException"/>
/// with a clear migration TODO until the payer-specific QNXT connector is implemented and accepted.
/// </summary>
/// <remarks>
/// TODO(qnxt-benefit-plan): integrate with the payer's licensed QNXT interface
/// (operation names come from that specification, not from this comment).
/// Reference docs: docs/integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md,
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
