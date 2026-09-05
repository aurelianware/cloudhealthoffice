using AuthorizationService.Models;
using Microsoft.Extensions.Options;

namespace AuthorizationService.Services.BenefitExclusion;

/// <summary>
/// Resolves the drug/service exclusions of the benefit plan applicable to an
/// authorization request. This is the "benefit plan / coverage rules" edge of
/// the authorization decision path; the CHO-native workflow owns the plan
/// configuration in Replace mode.
/// </summary>
public interface IBenefitExclusionCatalog
{
    /// <summary>
    /// Exclusions that apply to the given request, resolved from the member's
    /// tenant, line of business, and coverage. Empty when the applicable plan
    /// declares no exclusions (the common case) — nothing is then excluded.
    /// </summary>
    IReadOnlyList<Models.BenefitExclusion> ResolveExclusions(Authorization authorization);
}

/// <summary>
/// One plan's exclusion set plus the selector that scopes it to the applicable
/// members. A null selector field matches any value, so a platform-wide
/// exclusion (e.g. "drugs are out of medical PA scope") is expressed with all
/// selectors null.
/// </summary>
public sealed class BenefitPlanExclusionSet
{
    /// <summary>Tenant this set applies to; null = any tenant.</summary>
    public string? TenantId { get; set; }

    /// <summary>Line of business this set applies to; null = any LOB.</summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>Coverage/plan id this set applies to; null = any coverage.</summary>
    public string? CoverageId { get; set; }

    /// <summary>Optional human-readable plan identifier (audit only).</summary>
    public string? PlanId { get; set; }

    public List<Models.BenefitExclusion> Exclusions { get; set; } = new();
}

/// <summary>Options bound from configuration — the plan exclusion catalog.</summary>
public sealed class BenefitExclusionOptions
{
    public const string SectionName = "Cms0057:BenefitExclusions";

    public List<BenefitPlanExclusionSet> PlanExclusionSets { get; set; } = new();
}

/// <summary>
/// Configuration-driven <see cref="IBenefitExclusionCatalog"/>. Plan exclusions
/// are supplied through <see cref="BenefitExclusionOptions"/> (tenant/plan
/// scoped, synthetic in tests, per-engagement in a real deployment) rather than
/// hard-coded, so no example NDCs live in production code. Tenant isolation is
/// preserved: a set with a non-null <see cref="BenefitPlanExclusionSet.TenantId"/>
/// only applies to that tenant.
/// </summary>
public sealed class ConfiguredBenefitExclusionCatalog : IBenefitExclusionCatalog
{
    private readonly IOptions<BenefitExclusionOptions> _options;

    public ConfiguredBenefitExclusionCatalog(IOptions<BenefitExclusionOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<Models.BenefitExclusion> ResolveExclusions(Authorization authorization)
    {
        var sets = _options.Value.PlanExclusionSets;
        if (sets.Count == 0) return Array.Empty<Models.BenefitExclusion>();

        var resolved = new List<Models.BenefitExclusion>();
        foreach (var set in sets)
        {
            if (Applies(set, authorization))
                resolved.AddRange(set.Exclusions);
        }
        return resolved;
    }

    private static bool Applies(BenefitPlanExclusionSet set, Authorization authorization) =>
        (set.TenantId is null || string.Equals(set.TenantId, authorization.TenantId, StringComparison.Ordinal))
        && (set.LineOfBusiness is null || set.LineOfBusiness == authorization.LineOfBusiness)
        && (set.CoverageId is null || string.Equals(set.CoverageId, authorization.CoverageId, StringComparison.Ordinal));
}
