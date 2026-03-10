using CloudHealthOffice.RiskAdjustmentEngine.Data;
using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Applies CMS-published hierarchy rules to suppress less-severe HCCs when
/// a more severe HCC in the same disease group is present.
///
/// Rules are applied iteratively: suppression is transitive. If HCC 8 dominates
/// [9,10] and HCC 9 dominates [10], and both 8 and 9 are present, HCC 10 is
/// suppressed by both rules (only needs to be suppressed once).
/// </summary>
public class HccHierarchyResolver : IHccHierarchyResolver
{
    public HierarchyResolutionResult Resolve(IReadOnlySet<int> mappedHccs, HccModel model)
    {
        var rules = model == HccModel.CmsHccV28
            ? HccFactorData.CmsHccV28Hierarchies
            : [];  // HHS-HCC hierarchy not embedded in this release

        var suppressed = new HashSet<int>();

        foreach (var rule in rules)
        {
            if (mappedHccs.Contains(rule.Dominant))
            {
                foreach (var sub in rule.Subordinates)
                {
                    if (mappedHccs.Contains(sub))
                        suppressed.Add(sub);
                }
            }
        }

        var remaining = mappedHccs
            .Where(h => !suppressed.Contains(h))
            .ToHashSet();

        return new HierarchyResolutionResult
        {
            RemainingHccs  = remaining,
            SuppressedHccs = [.. suppressed.OrderBy(h => h)]
        };
    }
}
