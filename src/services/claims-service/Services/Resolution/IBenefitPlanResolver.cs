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

    /// <summary>
    /// In-network tier list for the plan, sorted by
    /// <see cref="ResolvedNetworkTier.TierLevel"/> ascending (1 = best).
    /// Populated by capability 5.6 from <c>BenefitPlan.networkTiers</c>;
    /// consumed by <c>NetworkCredentialingStage</c> to drive the
    /// "first matching tier wins" enforcement walk.
    ///
    /// <para>
    /// Empty for legacy plans whose tier list isn't populated; the
    /// enforcement stage treats an empty list as "out-of-network only"
    /// and applies the configured fail-mode for membership.
    /// </para>
    /// </summary>
    public IReadOnlyList<ResolvedNetworkTier> NetworkTiers { get; init; } =
        Array.Empty<ResolvedNetworkTier>();
}

/// <summary>
/// Pipeline-local projection of <c>BenefitPlanService.Models.NetworkTier</c>.
/// Carries only the fields the enforcement walk needs — <see cref="NetworkId"/>
/// is the cross-service handle into provider-service; tier name/level
/// surface on the enforcement outcome for audit.
/// </summary>
public sealed class ResolvedNetworkTier
{
    public required string TierName { get; init; }
    public int TierLevel { get; init; }

    /// <summary>
    /// Reference to <c>Organization.OrganizationId</c> in provider-service.
    /// Nullable during the BP 5.5 → hard-validation rollout window;
    /// the enforcement stage skips tiers whose NetworkId is null and
    /// emits a soft-validation telemetry signal.
    /// </summary>
    public string? NetworkId { get; init; }
}
