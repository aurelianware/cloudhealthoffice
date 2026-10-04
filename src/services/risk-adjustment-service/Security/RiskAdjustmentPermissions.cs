namespace RiskAdjustmentService;

/// <summary>
/// Permissions for risk-adjustment-service (docs/security/service-auth-rollout-playbook.md).
/// Risk scores carry diagnosis codes (PHI), so neither reads nor writes are
/// widened beyond what the scores' users need.
/// </summary>
public static class RiskAdjustmentPermissions
{
    /// <summary>Scores, trends, measurement-year lists and summaries. Finance holds it.</summary>
    public const string Read = "risk-adjustment:read";

    /// <summary>
    /// Score upserts, RAF calculations, submission status and deletes:
    /// <c>risk-adjustment:write</c> only. Finance holds it (it calculates the
    /// scores and runs the CMS submissions); <c>finance:write</c> (ledger
    /// changes) does not reach risk scores.
    /// </summary>
    public const string Write = "risk-adjustment:write";
}
