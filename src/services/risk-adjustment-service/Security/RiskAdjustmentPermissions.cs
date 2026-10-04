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
    /// <c>risk-adjustment:write</c> (the playbook default) or <c>finance:write</c>.
    /// No built-in role holds risk-adjustment:write yet (only <c>*:*</c> and
    /// service tokens satisfy it); Finance, which reads the scores and runs the
    /// CMS submissions, holds finance:write.
    /// </summary>
    public const string Write = "risk-adjustment:write,finance:write";
}
