using BenefitPlanService.Models;

namespace BenefitPlanService.Adapters;

/// <summary>
/// Abstraction for benefit-plan retrieval platforms.
/// Each tenant can be configured to use a different adapter (CHO, QNXT, Facets, HealthEdge, ...).
/// The adapter normalizes platform-specific responses into a common, vendor-neutral format
/// designed to project cleanly onto a future FHIR <c>InsurancePlan</c> resource (Section 5.8).
/// </summary>
/// <remarks>
/// Mirrors <c>EligibilityService.Adapters.IEligibilityAdapter</c>. The selection mechanism
/// (factory consults tenant-service config and falls back to "cho") is identical.
/// </remarks>
public interface IBenefitPlanAdapter
{
    /// <summary>
    /// Platform identifier matching <c>BenefitPlanConfig.Platform</c> on the tenant.
    /// Resolution by the factory is case-insensitive.
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// Return the canonical (latest published) version of a plan.
    /// Returns a response with <c>Plan == null</c> when not found so callers can map to 404.
    /// </summary>
    Task<BenefitPlanAdapterResponse> GetPlanAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Return a specific version of a plan identified by <see cref="BenefitPlanAdapterRequest.VersionId"/>.
    /// Returns a response with <c>Plan == null</c> when the version is not found.
    /// </summary>
    Task<BenefitPlanAdapterResponse> GetPlanVersionAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Return a portal-facing categorized view of the plan as of <see cref="BenefitPlanAdapterRequest.ServiceDate"/>.
    /// Returns a response with <c>View == null</c> when the plan is not found.
    /// </summary>
    Task<MemberBenefitViewAdapterResponse> GetMemberBenefitViewAsync(
        BenefitPlanAdapterRequest request, CancellationToken ct = default);
}
