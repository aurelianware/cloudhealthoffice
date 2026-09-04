using FluentAssertions;

namespace Cms0057Acceptance.Tests;

/// <summary>
/// GAP tests — assert that the per-engagement QNXT source-system adapters are
/// still <see cref="NotImplementedException"/> stubs. These are honest evidence
/// that a scenario's QNXT binding is engagement work, NOT a papered-over green
/// test. If a QNXT adapter is implemented for real, the matching test here
/// starts failing and must be replaced with a live-mode acceptance test.
///
/// Traceability:
///   claims-service     src/services/claims-service/Adapters/QnxtClaimAdapter.cs
///   provider-service   src/services/provider-service/Adapters/QnxtProviderAdapter.cs
///   benefit-plan       src/services/benefit-plan-service/Adapters/QnxtBenefitPlanAdapter.cs
///   authorization      src/services/authorization-service/Adapters/QnxtAuthorizationAdapter.cs
/// </summary>
public class GapAdapterTests
{
    [Fact]
    [Trait("Scenario", "PAS-01")]
    [Trait("Kind", "GAP")]
    public void PAS01_QnxtBenefitPlanAdapter_IsNotImplementedStub()
    {
        // GAP: CRD benefit/auth-required lookup against QNXT is engagement work.
        // The acceptance path for PAS-01 runs against the CHO rule store / Demo
        // mode (see CrdCoverageRequirementsTests); this asserts the QNXT seam is
        // still a stub so we never claim "production ready against QNXT".
        var adapter = new global::BenefitPlanService.Adapters.QnxtBenefitPlanAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action act = () => adapter.GetPlanAsync(null!);
        act.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT benefit plan adapter not yet implemented*");
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Kind", "GAP")]
    public void PAS03_QnxtAuthorizationAdapter_IsNotImplementedStub()
    {
        // GAP: creating an authorization in QNXT (AUTH_CREATE) is engagement
        // work. CHO ships the FHIR PAS surface + CHO-native authorization store;
        // PAS-03's happy path (PasSubmitTests) runs against that CHO path.
        var adapter = new global::AuthorizationService.Adapters.QnxtAuthorizationAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action create = () => adapter.CreateAuthorizationAsync(
            new global::AuthorizationService.Models.Authorization());
        create.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT authorization adapter not yet implemented*");

        Action status = () => adapter.GetAuthorizationByNumberAsync(
            AcceptanceContext_TenantId, "PAS-TEST-001");
        status.Should().Throw<NotImplementedException>();
    }

    [Fact]
    [Trait("Scenario", "PROV-01")]
    [Trait("Kind", "GAP")]
    public void PROV01_QnxtProviderAdapter_IsNotImplementedStub()
    {
        // GAP: pulling provider directory data from QNXT is engagement work.
        var adapter = new global::ProviderService.Adapters.QnxtProviderAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action act = () => adapter.GetProviderAsync(null!);
        act.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT provider adapter not yet implemented*");
    }

    [Fact]
    [Trait("Scenario", "PAT-01")]
    [Trait("Kind", "GAP")]
    public void PAT01_QnxtClaimAdapter_IsNotImplementedStub()
    {
        // GAP: reading member claims from QNXT is engagement work. PAT-01's
        // acceptance path runs against the CHO claims/EOB projection.
        var adapter = new global::ClaimsService.Adapters.QnxtClaimAdapter();
        adapter.Platform.Should().Be("qnxt");

        Action act = () => adapter.SearchClaimsForMemberAsync(null!);
        act.Should().Throw<NotImplementedException>()
            .WithMessage("*QNXT claim adapter not yet implemented*");
    }

    private const string AcceptanceContext_TenantId = "demo-tenant";
}
