using FluentAssertions;

namespace Cms0057Acceptance.Tests;

/// <summary>
/// Integration-capability GAP tests — assert that the per-engagement QNXT
/// source-system adapters are still <see cref="NotImplementedException"/> stubs.
/// These are the AUGMENT dimension: a QNXT integration is engagement work.
/// They are NOT product-capability gaps — where Cloud Health Office has a
/// native path, the same scenario passes in Replace mode (see the
/// Authorization*ModeTests and the CHO-native scenario tests).
///
/// The PAS-03 authorization Augment GAP now lives in
/// AuthorizationBackendSelectionTests (it is asserted through the real backend
/// selector), so it is not duplicated here.
///
/// Traceability:
///   claims-service     src/services/claims-service/Adapters/QnxtClaimAdapter.cs
///   provider-service   src/services/provider-service/Adapters/QnxtProviderAdapter.cs
///   benefit-plan       src/services/benefit-plan-service/Adapters/QnxtBenefitPlanAdapter.cs
/// </summary>
public class GapAdapterTests
{
    [Fact]
    [Trait("Scenario", "PAS-01")]
    [Trait("Backend", "Augment")]
    [Trait("Kind", "GAP")]
    public void PAS01_QnxtBenefitPlanAdapter_IsNotImplementedStub()
    {
        // GAP (integration): CRD benefit/auth-required lookup against QNXT is
        // engagement work. PAS-01's product path passes against the CHO rule
        // store (see CrdCoverageRequirementsTests).
        var adapter = new global::BenefitPlanService.Adapters.QnxtBenefitPlanAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action act = () => adapter.GetPlanAsync(null!);
        act.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT benefit plan adapter not yet implemented*");
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Backend", "Augment")]
    [Trait("Kind", "GAP")]
    public void PROV01_QnxtProviderAdapter_IsNotImplementedStub()
    {
        // GAP (integration): pulling provider directory data from QNXT is
        // engagement work.
        var adapter = new global::ProviderService.Adapters.QnxtProviderAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action act = () => adapter.GetProviderAsync(null!);
        act.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT provider adapter not yet implemented*");
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Backend", "Augment")]
    [Trait("Kind", "GAP")]
    public void PAT01_QnxtClaimAdapter_IsNotImplementedStub()
    {
        // GAP (integration): reading member claims from QNXT is engagement work.
        // PAT-01's product path runs against the CHO claims/EOB projection.
        var adapter = new global::ClaimsService.Adapters.QnxtClaimAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action act = () => adapter.SearchClaimsForMemberAsync(null!);
        act.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT claim adapter not yet implemented*");
    }
}
