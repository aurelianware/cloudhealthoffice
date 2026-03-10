using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Applies CMS hierarchy rules to a set of mapped HCC categories.
/// When a more severe HCC is present, less severe HCCs in the same disease
/// group are suppressed to prevent double-counting.
/// </summary>
public interface IHccHierarchyResolver
{
    /// <summary>
    /// Returns the subset of <paramref name="mappedHccs"/> that survive hierarchy
    /// resolution, plus the set of HCC codes that were suppressed.
    /// </summary>
    HierarchyResolutionResult Resolve(IReadOnlySet<int> mappedHccs, HccModel model);
}

public record HierarchyResolutionResult
{
    /// <summary>HCC codes that remain after hierarchy is applied.</summary>
    public IReadOnlySet<int> RemainingHccs { get; init; } = new HashSet<int>();

    /// <summary>HCC codes that were suppressed by a dominant HCC in the same group.</summary>
    public IReadOnlyList<int> SuppressedHccs { get; init; } = [];
}
