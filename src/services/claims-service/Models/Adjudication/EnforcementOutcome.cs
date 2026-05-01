namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Discriminator for the kind of enforcement check that produced the
/// outcome.
/// </summary>
public enum EnforcementCheck
{
    Membership,
    Credentialing,
}

/// <summary>
/// Decision the enforcement stage drew for a single check.
/// </summary>
public enum EnforcementDecision
{
    /// <summary>Check passed — claim continues.</summary>
    Allow,

    /// <summary>Check failed terminally — pipeline short-circuits to PersistenceStage.</summary>
    Deny,

    /// <summary>Check pended — pipeline continues so subsequent stages can decorate before human review.</summary>
    Pend,

    /// <summary>
    /// Soft-validation observation — no policy effect. Captured for
    /// telemetry only so operators can quantify what FailClosed/FailOpen
    /// would have done before flipping the posture.
    /// </summary>
    Observe,
}

/// <summary>
/// Per-check enforcement outcome accumulated on
/// <see cref="Services.Adjudication.ClaimAdjudicationContext.EnforcementOutcomes"/>.
/// One entry per (check × resolution path); the stage emits a Membership
/// outcome and a Credentialing outcome per claim.
/// </summary>
public sealed record EnforcementOutcome(
    EnforcementCheck Check,
    EnforcementDecision Decision,
    string Mode,
    string? Reason,
    DateTime AsOfDate,
    string? NetworkId = null,
    string? TierName = null,
    int? TierLevel = null);
