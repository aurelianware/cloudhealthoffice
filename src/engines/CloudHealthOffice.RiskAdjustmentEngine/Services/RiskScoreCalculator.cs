using CloudHealthOffice.RiskAdjustmentEngine.Data;
using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Computes the risk score:
///   FinalRiskScore = DemographicFactor + Σ(HCC relative factors)
///
/// The demographic factor covers the baseline cost of a member in a given
/// age/sex/segment cell — independent of any diagnosed conditions.
/// Each surviving HCC adds its relative factor on top.
///
/// CMS normalizes the risk scores plan-wide, but that normalization step
/// occurs at the plan level (outside this engine).
/// </summary>
public class RiskScoreCalculator : IRiskScoreCalculator
{
    public RiskScoreResult Calculate(
        RiskScoreInput input,
        Dictionary<string, int?> diagnosisToHccMap,
        HierarchyResolutionResult hierarchyResult)
    {
        // ── Demographic factor ────────────────────────────────────────────
        var demoFactor = LookupDemographicFactor(input);

        // ── HCC contributions ─────────────────────────────────────────────
        var categories = input.Model == HccModel.CmsHccV28
            ? HccFactorData.CmsHccV28Categories
            : HccFactorData.HhsHccCategories;

        // Build a map from HCC code → source diagnosis codes for audit
        var hccToSourceDx = new Dictionary<int, List<string>>();
        foreach (var (dx, hcc) in diagnosisToHccMap)
        {
            if (hcc.HasValue && hierarchyResult.RemainingHccs.Contains(hcc.Value))
            {
                if (!hccToSourceDx.TryGetValue(hcc.Value, out var list))
                    hccToSourceDx[hcc.Value] = list = [];
                list.Add(dx);
            }
        }

        var contributions = new List<HccContribution>();
        var totalHccFactor = 0m;

        foreach (var hccCode in hierarchyResult.RemainingHccs.OrderBy(h => h))
        {
            if (!categories.TryGetValue(hccCode, out var catInfo))
                continue; // HCC in crosswalk but not in factor table (add-on HCC or not in subset)

            contributions.Add(new HccContribution
            {
                CategoryCode       = hccCode,
                Description        = catInfo.Description,
                RelativeFactor     = catInfo.Factor,
                SourceDiagnosisCodes = hccToSourceDx.GetValueOrDefault(hccCode, [])
            });

            totalHccFactor += catInfo.Factor;
        }

        var finalScore = demoFactor + totalHccFactor;

        return new RiskScoreResult
        {
            MemberId           = input.MemberId,
            Model              = input.Model,
            Segment            = input.Segment,
            DemographicFactor  = demoFactor,
            HccContributions   = contributions,
            TotalHccFactor     = totalHccFactor,
            FinalRiskScore     = finalScore,
            DiagnosisToHccMap  = diagnosisToHccMap,
            SuppressedHccs     = [.. hierarchyResult.SuppressedHccs]
        };
    }

    private static decimal LookupDemographicFactor(RiskScoreInput input)
    {
        // Currently only Community Non-Dual factors are embedded
        var factors = HccFactorData.CmsHccV28DemographicFactors;

        var match = factors.FirstOrDefault(f =>
            f.Gender  == input.Gender &&
            f.Segment == input.Segment &&
            input.AgeAsOfPaymentYear >= f.AgeFrom &&
            input.AgeAsOfPaymentYear <= f.AgeTo);

        // Fall back to highest age band if no match (age > 95)
        if (match is null)
        {
            match = factors
                .Where(f => f.Gender == input.Gender && f.Segment == input.Segment)
                .OrderByDescending(f => f.AgeFrom)
                .FirstOrDefault();
        }

        return match?.Factor ?? 0m;
    }
}
