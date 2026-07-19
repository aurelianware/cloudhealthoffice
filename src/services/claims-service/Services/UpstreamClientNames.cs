namespace ClaimsService.Services;

/// <summary>
/// Named-<see cref="System.Net.Http.IHttpClientFactory"/> client
/// identifiers for cross-service calls from claims-service. Centralized
/// so capability 5.6 + 5.7 + 5.10 + 5.11 HTTP consumers don't drift on
/// string literals.
///
/// <para>
/// Existing typed clients (<see cref="Resolution.HttpBenefitPlanResolver.HttpClientName"/>,
/// <see cref="Resolution.HttpMemberResolver.HttpClientName"/>) keep their
/// own constants for back-compat; new clients added in 5.6 onward use
/// these.
/// </para>
/// </summary>
public static class UpstreamClientNames
{
    /// <summary>provider-service — capability 5.6 enforcement clients,
    /// BP 5.10 integrity gate, FL FMMIS provider service shim.</summary>
    public const string ProviderService = "ProviderService";

    /// <summary>benefit-plan-service — capability 5.5 plan resolver,
    /// 5.5 benefit calculation engine HTTP shim.</summary>
    public const string BenefitPlanService = "BenefitPlanService";

    /// <summary>member-service — capability 5.5 member resolver.</summary>
    public const string MemberService = "MemberService";

    /// <summary>terminology-service — authoritative coding-system display/crosswalk lookups.</summary>
    public const string TerminologyService = "TerminologyService";

    /// <summary>reference-data-service — fallback coding-system lookups.</summary>
    public const string ReferenceDataService = "ReferenceDataService";

    /// <summary>coverage-service — capability 5.8 Coordination of Benefits
    /// /member/{id}/cob lookup driving CHO-primary vs CHO-secondary
    /// detection.</summary>
    public const string CoverageService = "CoverageService";

    /// <summary>authorization-service — prior-authorization validation
    /// lookup used by adjudication before paying PA-sensitive claims.</summary>
    public const string AuthorizationService = "AuthorizationService";
}
