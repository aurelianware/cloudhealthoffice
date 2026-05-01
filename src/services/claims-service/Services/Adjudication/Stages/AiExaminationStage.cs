using System.Diagnostics;
using ClaimsService.Models.Adjudication;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.9 — replaces <see cref="AiExaminationStubStage"/>. Detects
/// NCCI bundling pends with at least one modifier-addressable edit failure
/// and emits a <c>ClaimPendedEvent</c> to Kafka <c>claims.pended.v1</c> so
/// claims-examiner-service picks the claim up asynchronously, calls Anthropic
/// with NCCI context, and writes a structured advisory recommendation back
/// via <c>PUT /api/claims/{id}/ai-examination</c>.
///
/// <para>
/// <b>Selective invocation (Decision 2 / Plan-First Gap A.1).</b> The
/// eligibility filter calls
/// <c>NcciEditFailureSnapshot.IsModifierAddressable()</c> directly — the
/// same predicate <c>ExaminerOrchestrator.SelectAddressableEdit</c> uses
/// on the consumer side, so producer and consumer can never drift. The
/// predicate checks rule attributes (<c>EditType="NcciPair"</c> AND
/// <c>RuleId="NE001"</c>); it does NOT check
/// <c>ModifierOverridePresent</c>. The point of AI examination is to
/// suggest a modifier when none is present — triggering on
/// "modifier already attached" would be backwards, since those claims
/// either pass NCCI cleanly or need a different remediation path
/// entirely. (The Plan-First Decision 2 text said
/// <c>ModifierOverridePresent</c> but the audit caught the inverted
/// semantic; A.1 ratification corrected it.)
/// </para>
///
/// <para>
/// <b>Async resume via separate subscription (Decision 1 / option 5).</b>
/// The stage portion is synchronous and millisecond-scale — just check
/// PendDetails and emit to Kafka. The AI work itself runs entirely in
/// claims-examiner-service. No synchronous Anthropic call inside the
/// orchestrator's message handler. Resume semantics flow via the
/// completion event on Service Bus topic <c>ai-examination-events</c>
/// (emitted by claims-examiner-service after successful write-back) —
/// no pipeline re-entry.
/// </para>
///
/// <para>
/// <b>Pend.Continue=true semantic (Decision 7).</b> The stage runs at
/// <see cref="Order"/> = 600 — after CoB at 500. NCCI pends from the 5.7
/// stage at Order=400 set <c>Pend.Continue=true</c> so subsequent stages
/// (CoB, AI) DO run; the orchestrator's loop only short-circuits on
/// Reject/Deny.
/// </para>
///
/// <para>
/// <b>Mode policy (Plan-First Gap H.3 / Gap E.1).</b>
/// <see cref="AiEnforcementMode.BestEffort"/> is the default and Phase 1
/// effective behavior — eligibility passes → emit + Pend with reason
/// <c>pending-ai-examination</c>; pipeline continues to Persistence.
/// <see cref="AiEnforcementMode.Disabled"/> is the operational kill
/// switch — the stage runs (so telemetry captures kill-switch usage)
/// but short-circuits to Pass with
/// <c>outcome="not_applicable"</c>, <c>reason="ai-disabled-by-policy"</c>.
/// <c>Required</c> is deferred until <c>IClaimEventPublisher</c> gains a
/// delivery signal (today's contract swallows all failures internally;
/// a stub mode would mislead operators).
/// </para>
///
/// <para>
/// <b>Race condition with PersistenceStage (Decision 16 / Plan-First
/// D.1).</b> The stage emits to Kafka at Order=600; PersistenceStage at
/// Order=999 then writes. If the consumer races persistence, the GET on
/// claims-service can 404. Mitigation lives on the consumer side
/// (bounded retry-on-404 in <c>ClaimsServiceClient.GetClaimAsync</c>);
/// the producer side stays simple. Operations alarm on
/// <c>cho.claims_examiner.claim_not_found</c> exhaustion to catch any
/// systemic latency regression.
/// </para>
///
/// <para>
/// <b>Not Required (Decision 7).</b> <see cref="IsRequired"/> = false;
/// AI examination is advisory. Disabling it via
/// <c>EnabledStages</c> degrades cleanly — NCCI pends still flow to the
/// human work queue via <c>PendDetails.PendCode</c>; only AI advisory is
/// suppressed.
/// </para>
/// </summary>
public sealed class AiExaminationStage : IClaimAdjudicationStage
{
    public const string StageName = "AiExamination";

    /// <summary>Stable pend reason emitted on Triggered. Phase 1 work-queue
    /// / telemetry consumers depend on this exact string.</summary>
    public const string PendingAiExaminationReason = "pending-ai-examination";

    /// <summary>Stable telemetry reason for the operational kill switch.</summary>
    public const string DisabledByPolicyReason = "ai-disabled-by-policy";

    /// <summary>Stable telemetry reason for non-NCCI pends (or no pend at all).</summary>
    public const string NotApplicableNoPendDetailsReason = "not-applicable-no-pend-details";
    public const string NotApplicableNonNcciReason = "not-applicable-non-ncci-pend";

    /// <summary>Stable telemetry reason when no edit failure is modifier-addressable.</summary>
    public const string NoModifierAddressableEditsReason = "no-modifier-addressable-edits";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly IClaimEventPublisher _eventPublisher;
    private readonly TenantEnforcementPolicyOptions _options;
    private readonly ILogger<AiExaminationStage> _logger;

    public AiExaminationStage(
        IClaimEventPublisher eventPublisher,
        IOptions<TenantEnforcementPolicyOptions> options,
        ILogger<AiExaminationStage> logger)
    {
        _eventPublisher = eventPublisher;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 600;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity(
            "Adjudication.AiExamination",
            ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);
        activity?.SetTag("ai_examination.mode", _options.AiMode.ToString());

        // Operational kill switch (Gap E.1) — stage runs, telemetry
        // captures usage, no Kafka emission.
        if (_options.AiMode == AiEnforcementMode.Disabled)
        {
            activity?.SetTag("ai_examination.outcome", "not_applicable");
            activity?.SetTag("ai_examination.reason", DisabledByPolicyReason);
            context.AiExaminationResult = new AiExaminationOutcome
            {
                Status = AiInvocationStatus.NotApplicable,
                Reason = DisabledByPolicyReason,
                EligibleEditFailureCount = 0,
            };
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        // Eligibility filter — NCCI pend present?
        if (context.PendDetails is null)
        {
            activity?.SetTag("ai_examination.outcome", "not_applicable");
            activity?.SetTag("ai_examination.reason", NotApplicableNoPendDetailsReason);
            context.AiExaminationResult = new AiExaminationOutcome
            {
                Status = AiInvocationStatus.NotApplicable,
                Reason = NotApplicableNoPendDetailsReason,
                EligibleEditFailureCount = 0,
            };
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        if (!string.Equals(context.PendDetails.PendCode, "NCCI", StringComparison.OrdinalIgnoreCase))
        {
            activity?.SetTag("ai_examination.outcome", "not_applicable");
            activity?.SetTag("ai_examination.reason", NotApplicableNonNcciReason);
            activity?.SetTag("ai_examination.pend_code", context.PendDetails.PendCode);
            context.AiExaminationResult = new AiExaminationOutcome
            {
                Status = AiInvocationStatus.NotApplicable,
                Reason = NotApplicableNonNcciReason,
                EligibleEditFailureCount = 0,
            };
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        // Plan-First Gap A.1 — mirror ExaminerOrchestrator's
        // SelectAddressableEdit predicate exactly. Calling the snapshot's
        // helper directly keeps producer and consumer aligned with a
        // single source of truth.
        var eligibleCount = context.PendDetails.EditFailures
            .Count(e => e.IsModifierAddressable());
        activity?.SetTag("ai_examination.eligible_edits_count", eligibleCount);

        if (eligibleCount == 0)
        {
            activity?.SetTag("ai_examination.outcome", "skipped");
            activity?.SetTag("ai_examination.reason", NoModifierAddressableEditsReason);
            context.AiExaminationResult = new AiExaminationOutcome
            {
                Status = AiInvocationStatus.Skipped,
                Reason = NoModifierAddressableEditsReason,
                EligibleEditFailureCount = 0,
            };
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        // Triggered — emit to Kafka. The publisher swallows all transient
        // failures internally (degraded-mode posture from prior PRs); a
        // try/catch here is belt-and-suspenders and won't fire under the
        // documented contract. We still wrap to defend against future
        // contract changes that might surface failures.
        try
        {
            var domainClaim = context.Claim.ToClaim();
            // Carry the deterministic PendDetails populated by 5.7
            // NcciEditsStage onto the domain claim; the publisher
            // copies them into the event payload.
            if (context.PendDetails is not null)
            {
                domainClaim.PendDetails = context.PendDetails;
            }
            await _eventPublisher
                .PublishClaimPendedAsync(domainClaim, context.TenantId, ct)
                .ConfigureAwait(false);
            activity?.SetTag("ai_examination.kafka_emission", "success");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defensive — under today's contract this branch is
            // unreachable (publisher swallows everything). Log and
            // continue with Triggered: the claim is still pended for
            // human review via PendDetails; AI advisory is the only
            // thing lost.
            _logger.LogWarning(ex,
                "Unexpected exception from PublishClaimPendedAsync for claim {ClaimVersionId}; recording as Triggered (advisory may not arrive)",
                SanitizeForLog(context.ClaimVersionId));
            activity?.SetTag("ai_examination.kafka_emission", "exception");
        }

        activity?.SetTag("ai_examination.outcome", "triggered");
        context.AiExaminationResult = new AiExaminationOutcome
        {
            Status = AiInvocationStatus.Triggered,
            Reason = PendingAiExaminationReason,
            EligibleEditFailureCount = eligibleCount,
        };

        return ClaimAdjudicationStageResult.Pend(StageName, PendingAiExaminationReason);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
