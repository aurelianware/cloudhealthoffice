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

/// <summary>
/// Posture <see cref="Services.Adjudication.Stages.CoordinationOfBenefitsStage"/>
/// adopts when CHO is detected as <em>secondary</em> (or tertiary) — i.e.
/// another payer holds primary responsibility for the claim. Phase 1 ships
/// CHO-primary adjudication only; the engine's
/// <c>CobCalculationService</c> for CHO-secondary calculation is registered
/// but not exercised by the stage (Phase 2 priorEob work). Capability 5.8.
///
/// <para>
/// Different default than the other modes: pending is the correct posture
/// when a capability is genuinely unimplemented — ops gets a queue with a
/// structured pend reason (<c>cob-secondary-not-supported-phase-1</c>)
/// rather than a denial that silently masks Phase 2 sizing. Coverage-service
/// degradation always pends regardless of the mode (see
/// <see cref="Services.Adjudication.Stages.CoordinationOfBenefitsStage"/>);
/// "unable to determine coverage state" isn't structurally a denial.
/// </para>
/// </summary>
public enum CobEnforcementMode
{
    /// <summary>
    /// Default — CHO-secondary scenarios produce a Pend outcome with the
    /// stable pend reason <c>cob-secondary-not-supported-phase-1</c> so
    /// the work queue and Phase-2 sizing telemetry both pick them up.
    /// Pipeline continues so downstream stages can decorate the audit
    /// trail (mirrors <see cref="NcciEnforcementMode.PendForReview"/>).
    /// </summary>
    PendForSecondary,

    /// <summary>
    /// CHO-secondary scenarios produce a terminal Deny outcome; pipeline
    /// short-circuits to PersistenceStage. Selected by tenants who want
    /// hard-block on secondary-payer claims rather than queueing them
    /// for Phase 2. Coverage-service degradation still pends (the absence
    /// of coverage data is not a denial).
    /// </summary>
    Deny,

    /// <summary>
    /// CHO-secondary scenarios are recorded on the audit trail (telemetry
    /// + <see cref="ClaimAdjudicationContext.CobResult"/>) but the stage
    /// returns Pass. Used during initial rollout to capture
    /// CHO-secondary frequency without affecting payment flow.
    /// </summary>
    SoftValidation,
}

/// <summary>
/// Posture <see cref="Services.Adjudication.Stages.AiExaminationStage"/>
/// adopts when handling NCCI bundling pends with modifier-addressable
/// edits. Capability 5.9.
///
/// <para>
/// <b>Required mode deferred (Plan-First Gap H.3).</b> The current
/// <c>IClaimEventPublisher.PublishClaimPendedAsync</c> contract swallows
/// all Kafka producer failures internally and returns <c>Task</c>; the
/// stage cannot observe whether the event actually reached the broker.
/// A <c>Required</c> mode that forks Pend-vs-Pass on degraded Kafka
/// would be functionally identical to <see cref="BestEffort"/> until the
/// publisher gains a delivery signal — shipping it as a stub would
/// mislead operators reading the enum. <c>Required</c> lands as an
/// additive enum value in a focused follow-up once the publisher
/// contract evolves (e.g., <c>Task&lt;bool&gt; TryPublishClaimPendedAsync</c>
/// or an <c>IsAvailable</c> probe).
/// </para>
/// </summary>
public enum AiEnforcementMode
{
    /// <summary>
    /// Default — eligibility filter passes → stage emits the Kafka event
    /// and returns Pend with reason <c>pending-ai-examination</c>;
    /// pipeline continues to PersistenceStage. AI examination is
    /// advisory; the absence of a recommendation never blocks claim
    /// processing because the pend is already structured (NCCI failures
    /// have a human work-queue path independent of AI).
    /// </summary>
    BestEffort,

    /// <summary>
    /// Operational kill switch. Stage runs but short-circuits to Pass
    /// with telemetry tag <c>outcome="not_applicable"</c>,
    /// <c>reason="ai-disabled-by-policy"</c> (Plan-First Gap E.1) so
    /// dashboards show kill-switch usage at a glance. Used during
    /// incident response — distinct from removing the stage from
    /// <c>EnabledStages</c>, which would also suppress telemetry.
    /// </summary>
    Disabled,
}
