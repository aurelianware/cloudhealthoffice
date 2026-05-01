namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Posture <see cref="Services.Adjudication.Stages.NetworkCredentialingStage"/>
/// adopts when the network-membership check is degraded (provider-service
/// HTTP failure, missing data, timeout). Capability 5.6.
/// </summary>
public enum NetworkEnforcementMode
{
    /// <summary>Deny the claim with a structured reason. Default — protects payer.</summary>
    FailClosed,

    /// <summary>Pend the claim for human review with a structured reason. Preserves availability.</summary>
    FailOpen,

    /// <summary>
    /// Pass the claim AND emit telemetry capturing what would have
    /// happened under FailClosed/FailOpen. Used during initial rollout
    /// to catch edge cases before hard policy lands.
    /// </summary>
    SoftValidation,
}

/// <summary>
/// Posture for the credentialing-status check when degraded. Same
/// trichotomy as <see cref="NetworkEnforcementMode"/>; modeled as a
/// distinct enum so per-domain policy can be tuned independently
/// (e.g. FailClosed on credentialing while FailOpen on membership while
/// the network roster is being backfilled).
/// </summary>
public enum CredentialingEnforcementMode
{
    FailClosed,
    FailOpen,
    SoftValidation,
}

/// <summary>
/// Posture <see cref="Services.Adjudication.Stages.NcciEditsStage"/> adopts
/// when the NCCI / MUE engine produces edit failures. Capability 5.7.
///
/// <para>
/// Different default than <see cref="NetworkEnforcementMode"/> /
/// <see cref="CredentialingEnforcementMode"/>: NCCI failures often have
/// a legitimate -59/X{EPSU} modifier-override path, so auto-denial
/// without human review is operationally harsh. The work queue is the
/// right channel for "this might be a bundling violation, but might be
/// a legitimate distinct-procedure case" — hence
/// <see cref="PendForReview"/> as the production default.
/// </para>
/// </summary>
public enum NcciEnforcementMode
{
    /// <summary>
    /// Default — failed edits produce a Pend outcome and surface in the
    /// work queue for human review (and, for NE001 with modifier
    /// addressability, the AI examiner). Pipeline continues so
    /// downstream stages can decorate the audit trail.
    /// </summary>
    PendForReview,

    /// <summary>
    /// Failed edits produce a terminal Deny outcome; pipeline
    /// short-circuits to PersistenceStage. Selected by tenants confident
    /// their NCCI configuration is mature enough for hard auto-denial.
    /// </summary>
    Deny,

    /// <summary>
    /// Failed edits are recorded on the audit trail but the stage
    /// returns Pass. Used during initial rollout to capture which
    /// claims would have pended/denied without altering payment flow.
    /// </summary>
    SoftValidation,
}
