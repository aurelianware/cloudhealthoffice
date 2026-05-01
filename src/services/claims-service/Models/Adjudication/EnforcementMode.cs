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
