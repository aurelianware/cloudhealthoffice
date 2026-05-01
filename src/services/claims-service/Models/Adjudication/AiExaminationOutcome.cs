namespace ClaimsService.Models.Adjudication;

/// <summary>
/// Outcome of <see cref="Services.Adjudication.Stages.AiExaminationStage"/>
/// (capability 5.9). Lives on
/// <see cref="Services.Adjudication.ClaimAdjudicationContext.AiExaminationResult"/>;
/// not persisted on <see cref="AdjudicationResult"/>. Phase 1 α posture
/// mirrors 5.4 scrubbing / 5.8 CoB — context-only, telemetry-driven.
///
/// <para>
/// The actual AI recommendation is written back to <c>Claim.AiExamination</c>
/// asynchronously by claims-examiner-service via the existing
/// <c>PUT /api/claims/{id}/ai-examination</c> endpoint. This outcome
/// captures only what the pipeline stage decided about <em>invocation</em>:
/// did the eligibility filter pass, was the Kafka event emitted, and how
/// many edit failures were eligible.
/// </para>
/// </summary>
public class AiExaminationOutcome
{
    public AiInvocationStatus Status { get; init; }

    /// <summary>
    /// Stable machine reason code for telemetry / work-queue triage.
    /// Recognized values:
    /// <c>not-applicable-no-pend-details</c>,
    /// <c>not-applicable-non-ncci-pend</c>,
    /// <c>ai-disabled-by-policy</c>,
    /// <c>no-modifier-addressable-edits</c>,
    /// <c>pending-ai-examination</c>.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Count of <c>EditFailures</c> entries that satisfied
    /// <c>NcciEditFailureSnapshot.IsModifierAddressable()</c>. Zero when the
    /// pend was non-NCCI or the filter found no eligible edits.
    /// </summary>
    public int EligibleEditFailureCount { get; init; }

    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Discriminator for <see cref="AiExaminationOutcome.Status"/>.
/// </summary>
public enum AiInvocationStatus
{
    /// <summary>
    /// No <c>PendDetails</c> on the context, or <c>PendCode</c> is not NCCI.
    /// AI examination is scoped to NCCI bundling pends only (Decision 2);
    /// other pend codes (AUTH, MEDREVIEW, COB, ...) have no AI consumer
    /// in scope.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// NCCI pend present but no edit failure satisfied
    /// <c>IsModifierAddressable()</c>. Examples: pure MUE failures
    /// (<c>RuleId="NE002"</c>), or NCCI pair edits with a
    /// <c>ModifierIndicator</c> the engine surfaces under a different
    /// <c>RuleId</c>. Pipeline continues; no Kafka emission.
    /// </summary>
    Skipped,

    /// <summary>
    /// Eligibility filter passed; <c>ClaimPendedEvent</c> was emitted to
    /// Kafka <c>claims.pended.v1</c>. Stage returns Pend; the claim is
    /// persisted in pended state and the AI consumer will write a
    /// recommendation back asynchronously.
    /// </summary>
    Triggered,
}
