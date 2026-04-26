namespace BenefitPlanService.Models;

/// <summary>
/// Vendor-neutral request envelope passed to any <see cref="Adapters.IBenefitPlanAdapter"/>.
/// A single shape covers all three read methods; per-method required fields are documented below.
/// </summary>
public class BenefitPlanAdapterRequest
{
    /// <summary>Tenant id resolved by the request middleware. Required by all methods.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Plan id (the persistent <c>Id</c> on the document, not <c>PlanId</c> business key). Required by all methods.</summary>
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
