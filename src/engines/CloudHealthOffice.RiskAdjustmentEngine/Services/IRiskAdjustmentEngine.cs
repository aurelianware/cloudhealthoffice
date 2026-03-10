using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Orchestrates the full risk adjustment pipeline:
///   1. ICD-10 → HCC mapping
///   2. Hierarchy resolution (suppress dominated HCCs)
///   3. Risk score calculation (demographic + HCC factors)
/// </summary>
public interface IRiskAdjustmentEngine
{
    /// <summary>
    /// Computes the risk score for a single member.
    /// </summary>
    RiskScoreResult ComputeRiskScore(RiskScoreInput input);

    /// <summary>
    /// Computes risk scores for a batch of members.
    /// Returns one result per input in the same order.
    /// </summary>
    IReadOnlyList<RiskScoreResult> ComputeRiskScores(IReadOnlyList<RiskScoreInput> inputs);
}
