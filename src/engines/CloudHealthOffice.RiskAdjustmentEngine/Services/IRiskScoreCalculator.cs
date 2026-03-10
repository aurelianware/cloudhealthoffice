using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Computes the final risk score from the member's demographic factor
/// and the HCC relative factors that survived hierarchy resolution.
/// </summary>
public interface IRiskScoreCalculator
{
    /// <summary>
    /// Looks up the demographic factor for the member's age/sex/segment cell,
    /// sums HCC relative factors, and returns the combined risk score.
    /// </summary>
    RiskScoreResult Calculate(RiskScoreInput input,
        Dictionary<string, int?> diagnosisToHccMap,
        HierarchyResolutionResult hierarchyResult);
}
