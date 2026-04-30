namespace ClaimsService.Services.Resolution;

/// <summary>
/// Resolves a benefit plan summary by id for the adjudication pipeline
/// (capability 5.5). Uses a typed HTTP client against benefit-plan-service
/// (<c>GET /api/v1/plans/{id}</c>) and is wrapped by
/// <see cref="CachingBenefitPlanResolver"/> in production for a 5-minute
/// per-tenant TTL. Mirrors the BP 5.6
/// <c>CachingServiceCategoryMappingRepository</c> pattern.
/// </summary>
public interface IBenefitPlanResolver
{
    /// <summary>
    /// Returns the plan summary, or <c>null</c> when the plan is missing
    /// or the call fails. Failure is non-throwing — adjudication degrades
    /// to a Reject outcome on missing plan rather than blowing up the
    /// pipeline run.
    /// </summary>
    Task<ResolvedBenefitPlan?> GetPlanAsync(string tenantId, string planId, CancellationToken ct = default);
}

/// <summary>
/// Pipeline-local view of a benefit plan. Carries only the fields the
/// adjudication pipeline needs; full plan documents live in
/// benefit-plan-service. Decoupled from <c>BenefitPlanService.Models.BenefitPlan</c>
/// so claims-service does not take a project reference on benefit-plan-service.
/// </summary>
public class ResolvedBenefitPlan
{
    /// <summary>String id matching the <c>BenefitPlan.Id</c> on benefit-plan-service.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// GUID id used by the engine (<c>BenefitResolutionRequest.BenefitPlanId</c>).
    /// Surfaced separately because the engine takes a Guid; benefit-plan-service
    /// keeps both surfaces in sync. May be <c>null</c> when the plan doc lacks
    /// a Guid id (legacy plans).
    /// </summary>
    public Guid? PlanGuid { get; init; }

    public string? PlanName { get; init; }
    public string? PlanType { get; init; }
}
