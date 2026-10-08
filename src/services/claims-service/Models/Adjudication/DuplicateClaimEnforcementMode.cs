namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Posture <see cref="Services.Adjudication.Stages.DuplicateClaimStage"/>
/// adopts for a detected duplicate. Configured separately for exact
/// duplicates (<see cref="TenantEnforcementPolicyOptions.ExactDuplicateMode"/>,
/// default <see cref="Deny"/>) and suspect duplicates
/// (<see cref="TenantEnforcementPolicyOptions.SuspectDuplicateMode"/>,
/// default <see cref="PendForReview"/>).
/// </summary>
public enum DuplicateClaimEnforcementMode
{
    /// <summary>
    /// Terminal Deny with CARC 18 (group CO, "exact duplicate claim/service");
    /// pipeline short-circuits to PersistenceStage. Applied at claim level
    /// only when EVERY service line resolves to Deny — a claim with a mix of
    /// duplicate and clean lines pends instead, because the pipeline has no
    /// partial line-level denial path ahead of benefit calculation.
    /// </summary>
    Deny,

    /// <summary>
    /// Pend for examiner review with pend code <c>DUPLICATE</c>; pipeline
    /// continues so downstream stages can decorate the audit trail.
    /// </summary>
    PendForReview,

    /// <summary>
    /// Findings are logged and tagged on telemetry but the stage returns
    /// Pass. Used during rollout to size duplicate volume without
    /// altering payment flow.
    /// </summary>
    SoftValidation,
}
