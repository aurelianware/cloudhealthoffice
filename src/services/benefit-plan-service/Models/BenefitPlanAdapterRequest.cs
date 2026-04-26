namespace BenefitPlanService.Models;

/// <summary>
/// Vendor-neutral request envelope passed to any <see cref="Adapters.IBenefitPlanAdapter"/>.
/// A single shape covers all three read methods; per-method required fields and identifier
/// semantics are documented on the individual properties below.
/// </summary>
public class BenefitPlanAdapterRequest
{
    /// <summary>Tenant id resolved by the request middleware. Required by all methods.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Plan identifier. Semantics differ by adapter method and follow the existing
    /// controller / repository conventions:
    /// <list type="bullet">
    ///   <item>
    ///     <c>GetPlanAsync</c> — the persistent document <c>Id</c> (single-version
    ///     row primary key used by <c>GET /api/v1/plans/{id}</c>).
    ///   </item>
    ///   <item>
    ///     <c>GetPlanVersionAsync</c> — the business <c>PlanId</c> (version-chain key
    ///     used by <c>GET /api/v1/plans/{planId}/versions/{versionId}</c>).
    ///   </item>
    ///   <item>
    ///     <c>GetMemberBenefitViewAsync</c> — the persistent document <c>Id</c> (matches
    ///     <c>GET /api/v1/benefit-plans/{planId}/member-view</c>).
    ///   </item>
    /// </list>
    /// Required by all methods. Splitting this into two explicit fields
    /// (<c>DocumentId</c> + <c>PlanId</c>) is tracked as a follow-up; today the
    /// dual semantics mirror the historical controller and repository behaviour.
    /// </summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>
    /// Specific version identifier (ULID). Required by
    /// <c>GetPlanVersionAsync</c>; ignored otherwise.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>Optional subscriber id for member-scoped views.</summary>
    public string? SubscriberId { get; set; }

    /// <summary>
    /// Effective date used to resolve which version applies. Used by
    /// <c>GetMemberBenefitViewAsync</c>; defaults to <see cref="DateTime.UtcNow"/> when null.
    /// </summary>
    public DateTime? ServiceDate { get; set; }

    /// <summary>
    /// Platform-specific configuration sourced from
    /// <c>BenefitPlanConfig.PlatformSettings</c> (e.g. QNXT base URL,
    /// Facets credential reference). Adapters read what they need; the
    /// factory passes the value through unchanged.
    /// </summary>
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}
