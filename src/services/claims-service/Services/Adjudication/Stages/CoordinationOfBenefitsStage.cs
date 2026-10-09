using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.8 — replaces <see cref="CoordinationOfBenefitsStubStage"/>.
/// Calls coverage-service's <c>/member/{id}/cob</c> endpoint to determine
/// whether CHO is the primary, secondary, or tertiary payer for the
/// claim's member, and produces a structured <see cref="CobOutcome"/> on
/// the context. Runs at <see cref="Order"/> = 275 — after pricing, before
/// <see cref="BenefitCalculationStage"/> (300) — so the payer order is
/// settled before the benefit engine prices the claim and writes
/// accumulators.
///
/// <para>
/// <b>Payer order (PR #1278).</b> Coverage-service and the 837 (2000B
/// SBR01, 2320/2430) are compared — see <see cref="ResolveLaterPayer"/> and
/// the decision table in docs/architecture/claim-cob-pipeline.md. When they
/// agree that CHO pays after another payer and the 837 carries complete
/// prior-payer data, the stage passes with <see cref="CobOutcome.ApplyCob"/>
/// and the benefit engine applies claim-level COB. Disagreement, duplicate
/// payer sequences and unplaceable 2430 loops pend in every mode; missing
/// prior-payer data keeps the Phase-1 mode-driven pend
/// (<c>cob-secondary-not-supported-phase-1</c>, a stable legacy code).
/// </para>
///
/// <list type="bullet">
///   <item><description>Coverage-service confirms no other coverage
///     (empty list / 404) → <c>Pass</c>;
///     <see cref="CobScenario.ChoPrimaryNoSecondary"/>.</description></item>
///   <item><description>All other entries are <c>"S"</c> / <c>"T"</c>
///     (CHO is implicit primary) → <c>Pass</c>;
///     <see cref="CobScenario.ChoPrimaryWithSecondary"/>.</description></item>
///   <item><description>Exactly one <c>"P"</c> entry (no other "S") →
///     <see cref="CobScenario.ChoSecondaryDetected"/>; mode-driven
///     outcome.</description></item>
///   <item><description>Multiple <c>"P"</c> entries OR one <c>"P"</c>
///     plus at least one <c>"S"</c> (CHO at position 3+) →
///     <see cref="CobScenario.ChoTertiaryDetected"/>; mode-driven
///     outcome.</description></item>
///   <item><description>Coverage-service degraded (<c>null</c> from the
///     client) → <c>Pend</c> in <c>PendForSecondary</c> AND <c>Deny</c>
///     modes (Decision 7 — "unable to determine coverage state" is
///     not structurally a denial); <c>Pass</c> in <c>SoftValidation</c>
///     mode with telemetry capturing the degradation. Either way
///     <see cref="ClaimAdjudicationContext.CobResult"/> is set with
///     <see cref="CobScenario.None"/> and the
///     <c>cob-coverage-service-unavailable</c> pend reason.</description></item>
/// </list>
///
/// <para>
/// <b>Required (Decision 2).</b> <see cref="IsRequired"/> = true. Disabling
/// COB enforcement would let CHO-secondary claims process as CHO-primary
/// — wrong on the wire. <c>CobMode = SoftValidation</c> no longer passes a
/// later-payer claim (PR #1278 re-review N4): it would price as primary a
/// claim this plan is not primary on. A later-payer claim without usable
/// COB data pends in <c>SoftValidation</c> and <c>PendForSecondary</c>, and
/// is denied in <c>Deny</c> — never paid. SoftValidation still passes a
/// coverage-service outage on a claim the 837 sends as primary.
/// </para>
///
/// <para>
/// <b>Engine surface (Decision 8).</b> The stage invokes
/// <see cref="IPayerOrderService.DetermineOrder"/> for audit-trail rule
/// labelling on detected CHO-secondary / CHO-tertiary scenarios. Phase 1
/// data-source gaps on <c>CobEntryResponse</c> (no birthday, no employment
/// status, no LGHP signal) mean the engine only differentiates Medicare
/// scenarios reliably; for commercial-primary cases the engine defaults to
/// <see cref="PayerOrderRule.ExplicitCoverageRecord"/> — which the stage
/// keeps because <c>CoverageSequence="P"</c> IS the explicit signal.
/// </para>
///
/// <para>
/// <b>Pend reason format.</b> <see cref="CobOutcome.PendReason"/> is the
/// stable machine reason code (<c>cob-secondary-not-supported-phase-1</c>,
/// <c>cob-coverage-service-unavailable</c>); the
/// <see cref="ClaimAdjudicationStageResult.Reason"/> is the human-readable
/// reason that surfaces on the work-queue UI.
/// </para>
///
/// <para>
/// <b>Pend-persistence defect fix.</b> This stage used to leave
/// <see cref="ClaimAdjudicationContext.PendDetails"/> untouched — the
/// channel was reserved for NCCI's deterministic edit-failure snapshots
/// (5.7) on the stated theory that "the work-queue UI uses the stage
/// result's human-readable reason" for COB. That theory doesn't hold:
/// <see cref="ClaimAdjudicationStageResult.Reason"/> lives only on the
/// in-flight <see cref="ClaimAdjudicationContext.StageResults"/> for the
/// duration of one Service Bus message handler — nothing persists it, so
/// no work-queue UI or examiner ever actually saw it. <see cref="CobPendCode"/>
/// (<c>"COB"</c>) was already a documented, expected
/// <see cref="PendDetails.PendCode"/> value and the work queue already has
/// a <c>CobRequired</c> bucket keyed on it
/// (<c>ClaimsController.GetWorkQueueSummary</c>) — this stage simply never
/// emitted it. Fixed: both Pend-producing paths (<see cref="BuildSecondaryOutcome"/>,
/// <see cref="BuildDegradedOutcome"/>) now populate <c>PendDetails</c>
/// unconditionally, mirroring <see cref="NcciEditsStage"/>'s existing
/// precedent of recording the deterministic snapshot regardless of
/// enforcement mode (an audit trail even when Deny mode ultimately denies
/// the claim instead of pending it).
/// </para>
/// </summary>
public sealed class CoordinationOfBenefitsStage : IClaimAdjudicationStage
{
    public const string StageName = "CoordinationOfBenefits";

    /// <summary>Stable pend-reason code for CHO-secondary detection.
    /// Phase 1 work-queue / telemetry consumers depend on this exact
    /// string; do not change without coordinated update.</summary>
    public const string SecondaryNotSupportedPendReason = "cob-secondary-not-supported-phase-1";

    /// <summary>Stable pend-reason code for coverage-service degradation.
    /// Distinguishes ops-triage signal from the structural Phase 2 hook.</summary>
    public const string CoverageServiceUnavailablePendReason = "cob-coverage-service-unavailable";

    /// <summary>Coverage-service and the 837 (2000B SBR01) disagree on this
    /// plan's payer order — e.g. coverage says primary and the 837 says
    /// secondary, or the reverse. Pends in every mode: never paid.</summary>
    public const string PayerOrderMismatchPendReason = "cob-payer-order-mismatch";

    /// <summary>Two 2320 loops claim the same payer sequence (or our own):
    /// the payer order is ambiguous. Pends in every mode.</summary>
    public const string DuplicatePayerSequencePendReason = "cob-duplicate-payer-sequence";

    /// <summary>A 2430 SVD01 names none of the claim's other payers (2330B
    /// NM109, REF*2U, REF*FY): its payment cannot be placed in the payer
    /// order. Pends in every mode rather than undercount prior payments.</summary>
    public const string UnmatchedLineAdjudicationPendReason = "cob-unmatched-line-adjudication";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> value for COB pends. Already a
    /// documented, recognized value (see <see cref="PendDetails.PendCode"/>'s
    /// own doc comment and the work queue's <c>CobRequired</c> bucket in
    /// <c>ClaimsController.GetWorkQueueSummary</c>) — no new pend vocabulary
    /// introduced here.
    /// </summary>
    public const string CobPendCode = "COB";

    /// <summary>Sentinel <see cref="InsuredInfo.PayerId"/> for CHO when
    /// constructing the engine input. <see cref="ResolvedBenefitPlan"/>
    /// has no payer-id field today (Phase 2 contract). Stable so
    /// telemetry consumers can identify CHO's slot in audit logs.</summary>
    private const string ChoPayerIdSentinel = "CHO";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly ICoverageClient _coverageClient;
    private readonly IPayerOrderService _payerOrder;
    private readonly TenantEnforcementPolicyOptions _options;
    private readonly ILogger<CoordinationOfBenefitsStage> _logger;

    public CoordinationOfBenefitsStage(
        ICoverageClient coverageClient,
        IPayerOrderService payerOrder,
        IOptions<TenantEnforcementPolicyOptions> options,
        ILogger<CoordinationOfBenefitsStage> logger)
    {
        _coverageClient = coverageClient;
        _payerOrder = payerOrder;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    /// <summary>
    /// 275 — after pricing (250), before benefit calculation (300). The payer
    /// order must be settled before the benefit engine prices the claim and
    /// writes accumulators: a claim this stage pends is priced read-only
    /// (no accumulator write), and only a claim this stage clears for COB is
    /// priced as a later payer. See the decision table in
    /// docs/architecture/claim-cob-pipeline.md.
    /// </summary>
    public int Order => 275;
    public bool IsRequired => true;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity(
            "Adjudication.CoordinationOfBenefits",
            ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);
        activity?.SetTag("cob.mode", _options.CobMode.ToString());

        var memberId = context.Claim.MemberId;
        if (string.IsNullOrWhiteSpace(memberId))
        {
            // Structural data-quality failure — earlier stages should
            // have caught this, but produce a deterministic Reject if a
            // claim with no member id reaches us. Mirrors 5.6's
            // missing-NPI Reject path.
            activity?.SetTag("cob.outcome", "reject");
            activity?.SetTag("cob.reason", "missing_member_id");
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                "Claim is missing MemberId; coordination-of-benefits lookup cannot run.");
        }

        if (context.ExaminerApproval?.PayerSequence is int examinerSequence)
            return ResolveExaminerPayerOrder(context, activity, examinerSequence);

        var serviceDate = ResolveEarliestServiceDate(context.Claim);

        IReadOnlyList<CobEntry>? entries;
        try
        {
            entries = await _coverageClient
                .GetCobEntriesAsync(context.TenantId, memberId, serviceDate, ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The HTTP client already swallows transport exceptions and
            // returns null. Anything reaching here is unexpected — log
            // and degrade.
            _logger.LogError(ex,
                "Unexpected exception calling coverage-service /cob for claim {ClaimVersionId}",
                SanitizeForLog(context.ClaimVersionId));
            entries = null;
        }

        // This plan's payer sequence as the 837 states it (2000B SBR01);
        // null when absent or "U" (unknown).
        var claimSequence = PayerResponsibility.ToSequence(context.Claim.PayerResponsibilityCode);
        activity?.SetTag("cob.claim_sequence", claimSequence);

        if (entries is null)
        {
            // Decision 7 — Pend regardless of mode. Coverage data
            // unavailable is not a denial signal. A claim the 837 sends us
            // as a later payer pends in every mode: the payer order cannot be
            // confirmed, so it is neither paid as primary nor as secondary.
            return BuildDegradedOutcome(context, activity, forcePend: claimSequence >= 2);
        }

        activity?.SetTag("cob.coverage_service", "success");
        activity?.SetTag("cob.entry_count", entries.Count);

        var classification = Classify(entries);
        activity?.SetTag("cob.outcome", ScenarioTag(classification.Scenario));

        switch (classification.Scenario)
        {
            case CobScenario.ChoPrimaryNoSecondary:
            case CobScenario.ChoPrimaryWithSecondary:
                // Coverage says primary; the 837 must not say otherwise.
                return claimSequence >= 2
                    ? BuildDataPend(context, activity, classification, PayerOrderMismatchPendReason,
                        $"Coverage records show Cloud Health Office as the primary payer, but the 837 (SBR01 " +
                        $"'{context.Claim.PayerResponsibilityCode}') submits it as payer {claimSequence}.")
                    : BuildPrimaryOutcome(context, activity, classification);

            case CobScenario.ChoSecondaryDetected:
            case CobScenario.ChoTertiaryDetected:
                return ResolveLaterPayer(context, activity, classification, entries, claimSequence);

            default:
                // CobScenario.None can't be reached from Classify (only the
                // degraded path produces None) — defensive default mirrors
                // the degraded path so a future enum addition fails safe.
                return BuildDegradedOutcome(context, activity);
        }
    }

    /// <summary>
    /// Coverage-service says this plan pays after another payer. Decision
    /// table (see docs/architecture/claim-cob-pipeline.md):
    /// <list type="bullet">
    ///   <item><description>The 837 gives no payer sequence (SBR01 absent /
    ///     U) → the Phase-1 posture: mode-driven pend (no prior-payer
    ///     data).</description></item>
    ///   <item><description>The 837 says primary, or a different later
    ///     position (coverage secondary vs 837 tertiary-or-later, or the
    ///     reverse) → pend in every mode, payer-order mismatch.</description></item>
    ///   <item><description>Duplicate 2320 sequences, or 2430 loops matching
    ///     no other payer → pend in every mode.</description></item>
    ///   <item><description>Agreed, but the 837 lacks a 2320 loop with a paid
    ///     amount (AMT*D or 2430 SVD) for every earlier sequence → mode-driven
    ///     pend.</description></item>
    ///   <item><description>Agreed with complete 2320 data → Pass with
    ///     <see cref="CobOutcome.ApplyCob"/>: BenefitCalculationStage prices
    ///     the claim as payer <see cref="CobOutcome.PayerSequence"/> and the
    ///     claim finalizes.</description></item>
    /// </list>
    /// </summary>
    private ClaimAdjudicationStageResult ResolveLaterPayer(
        ClaimAdjudicationContext context,
        Activity? activity,
        ScenarioClassification classification,
        IReadOnlyList<CobEntry> entries,
        int? claimSequence)
    {
        if (claimSequence is null)
            return BuildSecondaryOutcome(context, activity, classification, entries);

        var coverageIsSecondary = classification.Scenario == CobScenario.ChoSecondaryDetected;
        if (claimSequence == 1 || coverageIsSecondary != (claimSequence == 2))
        {
            return BuildDataPend(context, activity, classification, PayerOrderMismatchPendReason,
                $"Coverage records show Cloud Health Office as the " +
                $"{(coverageIsSecondary ? "secondary" : "tertiary-or-later")} payer, but the 837 (SBR01 " +
                $"'{context.Claim.PayerResponsibilityCode}') submits it as payer {claimSequence}.");
        }

        switch (PriorPayerDataProblem(context.Claim, claimSequence.Value))
        {
            case { Code: SecondaryNotSupportedPendReason }:
                return BuildSecondaryOutcome(context, activity, classification, entries);
            case { } problem:
                return BuildDataPend(context, activity, classification, problem.Code, problem.Message);
        }

        activity?.SetTag("cob.apply", true);
        context.CobResult = new CobOutcome
        {
            Scenario = classification.Scenario,
            PrimaryPayerName = classification.PrimaryPayerName,
            PrimaryPayerId = classification.PrimaryPayerId,
            IsMedicarePrimary = classification.IsMedicarePrimary,
            PendReason = null,
            AppliedRule = ResolveAppliedRule(context, entries),
            ApplyCob = true,
            PayerSequence = claimSequence,
        };
        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private sealed record DataProblem(string Code, string Message);

    /// <summary>
    /// Why the 837's prior-payer data cannot support pricing this plan as
    /// payer <paramref name="ourSequence"/>, or null when it can: duplicate
    /// sequences, 2430 loops matching no payer, or no 2320 with a paid amount
    /// (AMT*D or 2430 SVD) for some earlier sequence (reported with the
    /// legacy <see cref="SecondaryNotSupportedPendReason"/> code).
    /// </summary>
    private static DataProblem? PriorPayerDataProblem(AdapterClaim claim, int ourSequence)
    {
        var sequenced = (claim.OtherPayers ?? [])
            .Select(p => PayerResponsibility.ToSequence(p.PayerResponsibilityCode))
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .ToList();
        var duplicate = sequenced.GroupBy(s => s).FirstOrDefault(g => g.Count() > 1)?.Key
                        ?? (sequenced.Contains(ourSequence) ? ourSequence : null);
        if (duplicate is not null)
        {
            return new(DuplicatePayerSequencePendReason,
                $"The 837 lists payer sequence {duplicate} more than once (2320 SBR01 / 2000B SBR01); " +
                "the payer order is ambiguous.");
        }

        if (claim.UnmatchedOtherPayerLines is { Count: > 0 } unmatched)
        {
            return new(UnmatchedLineAdjudicationPendReason,
                $"{unmatched.Count} 2430 line adjudication(s) (SVD01 " +
                $"{string.Join(", ", unmatched.Select(u => u.PayerId ?? "?").Distinct())}) match no other payer's " +
                "2330B NM109, REF*2U or REF*FY; prior payments cannot be placed in the payer order.");
        }

        var missing = Enumerable.Range(1, ourSequence - 1).Where(seq =>
            !(claim.OtherPayers ?? []).Any(p =>
                PayerResponsibility.ToSequence(p.PayerResponsibilityCode) == seq
                && (p.PaidAmount is not null || p.LineAdjudications.Count > 0))).ToList();
        return missing.Count == 0
            ? null
            : new(SecondaryNotSupportedPendReason,
                $"The 837 carries no prior-payer data (2320 with AMT*D or 2430 SVD) for payer sequence(s) " +
                $"{string.Join(", ", missing)}.");
    }

    /// <summary>
    /// Examiner approval of a COB pend (<see cref="ExaminerApproval.PayerSequence"/>):
    /// the examiner confirmed the payer order. 1 → priced as primary. 2 or
    /// more → priced as that payer, which needs the prior payers' data on the
    /// 837 for every earlier sequence; otherwise the claim stays pended (the
    /// approval is refused) with the data problem as the reason.
    /// </summary>
    private ClaimAdjudicationStageResult ResolveExaminerPayerOrder(
        ClaimAdjudicationContext context, Activity? activity, int payerSequence)
    {
        activity?.SetTag("cob.examiner_payer_sequence", payerSequence);
        var classification = new ScenarioClassification(
            payerSequence switch
            {
                <= 1 => CobScenario.ChoPrimaryNoSecondary,
                2 => CobScenario.ChoSecondaryDetected,
                _ => CobScenario.ChoTertiaryDetected,
            },
            IsMedicarePrimary: false, PrimaryPayerName: null, PrimaryPayerId: null);

        if (payerSequence >= 2 && PriorPayerDataProblem(context.Claim, payerSequence) is { } problem)
        {
            return BuildDataPend(context, activity, classification, problem.Code,
                $"Examiner confirmed payer sequence {payerSequence}, but: {problem.Message}");
        }

        context.CobResult = new CobOutcome
        {
            Scenario = classification.Scenario,
            ApplyCob = payerSequence >= 2,
            PayerSequence = Math.Max(1, payerSequence),
            ConfirmedByExaminer = true,
        };
        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    /// <summary>
    /// A COB data / payer-order problem the stage cannot resolve: pend in
    /// every enforcement mode (never pay, never deny), with the reason on
    /// <see cref="ClaimAdjudicationContext.PendDetails"/> (pend code COB).
    /// </summary>
    private ClaimAdjudicationStageResult BuildDataPend(
        ClaimAdjudicationContext context,
        Activity? activity,
        ScenarioClassification classification,
        string reasonCode,
        string message)
    {
        activity?.SetTag("cob.outcome_mode", "pend");
        activity?.SetTag("cob.pend_reason", reasonCode);
        context.CobResult = new CobOutcome
        {
            Scenario = classification.Scenario,
            PrimaryPayerName = classification.PrimaryPayerName,
            PrimaryPayerId = classification.PrimaryPayerId,
            IsMedicarePrimary = classification.IsMedicarePrimary,
            PendReason = reasonCode,
            PayerSequence = PayerResponsibility.ToSequence(context.Claim.PayerResponsibilityCode),
        };
        context.PendDetails = new PendDetails
        {
            PendCode = CobPendCode,
            PendReason = TruncatePendReason($"{message} Reason code: {reasonCode}."),
            PendedAt = DateTime.UtcNow,
            EditFailures = new List<NcciEditFailureSnapshot>(),
        };
        return ClaimAdjudicationStageResult.Pend(StageName, message);
    }

    /// <summary>
    /// Build the <see cref="CobScenario"/> from the wire entries. Only the
    /// CoverageSequence string is consulted; the Medicare-primary signal
    /// is captured separately on <see cref="ScenarioClassification"/> so
    /// telemetry can differentiate Medicare-primary vs commercial-primary.
    /// </summary>
    internal static ScenarioClassification Classify(IReadOnlyList<CobEntry> entries)
    {
        if (entries.Count == 0)
        {
            return new ScenarioClassification(
                CobScenario.ChoPrimaryNoSecondary,
                IsMedicarePrimary: false,
                PrimaryPayerName: null,
                PrimaryPayerId: null);
        }

        var primaries = entries.Where(e => e.IsPrimary).ToList();
        var secondaries = entries.Where(e => e.IsSecondary).ToList();

        if (primaries.Count == 0)
        {
            // CHO is the implicit primary; other entries are sequenced
            // after CHO. Phase 2 sizing telemetry tracks this separately
            // from "no other coverage".
            return new ScenarioClassification(
                CobScenario.ChoPrimaryWithSecondary,
                IsMedicarePrimary: false,
                PrimaryPayerName: null,
                PrimaryPayerId: null);
        }

        // Pick the FIRST primary entry as the authoritative source for
        // PrimaryPayer{Name,Id}. Multiple primaries are an upstream data
        // anomaly; the tertiary-detection branch still fires.
        var firstPrimary = primaries[0];
        var isMedicarePrimary = primaries.Any(e => e.IsMedicare);

        var scenario = (primaries.Count >= 2 || secondaries.Count >= 1)
            ? CobScenario.ChoTertiaryDetected
            : CobScenario.ChoSecondaryDetected;

        return new ScenarioClassification(
            scenario,
            IsMedicarePrimary: isMedicarePrimary,
            PrimaryPayerName: firstPrimary.PayerName,
            PrimaryPayerId: firstPrimary.PayerId);
    }

    private ClaimAdjudicationStageResult BuildPrimaryOutcome(
        ClaimAdjudicationContext context,
        Activity? activity,
        ScenarioClassification classification)
    {
        context.CobResult = new CobOutcome
        {
            Scenario = classification.Scenario,
            PrimaryPayerName = classification.PrimaryPayerName,
            PrimaryPayerId = classification.PrimaryPayerId,
            IsMedicarePrimary = classification.IsMedicarePrimary,
            PendReason = null,
            AppliedRule = null,
        };
        activity?.SetTag("cob.medicare_primary", classification.IsMedicarePrimary);
        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private ClaimAdjudicationStageResult BuildSecondaryOutcome(
        ClaimAdjudicationContext context,
        Activity? activity,
        ScenarioClassification classification,
        IReadOnlyList<CobEntry> entries)
    {
        var appliedRule = ResolveAppliedRule(context, entries);
        activity?.SetTag("cob.applied_rule", appliedRule.ToString());
        activity?.SetTag("cob.medicare_primary", classification.IsMedicarePrimary);

        context.CobResult = new CobOutcome
        {
            Scenario = classification.Scenario,
            PrimaryPayerName = classification.PrimaryPayerName,
            PrimaryPayerId = classification.PrimaryPayerId,
            IsMedicarePrimary = classification.IsMedicarePrimary,
            PendReason = SecondaryNotSupportedPendReason,
            AppliedRule = appliedRule,
        };

        // Defect B fix — record the deterministic COB-secondary/tertiary
        // snapshot on PendDetails regardless of enforcement mode, mirroring
        // NcciEditsStage.ApplyFailureSnapshots. In Deny mode the claim still
        // ends up Denied (Deny outweighs Pend in the orchestrator's
        // precedence), but the audit trail explains why COB fired.
        context.PendDetails = new PendDetails
        {
            PendCode = CobPendCode,
            PendReason = TruncatePendReason(
                $"Cloud Health Office is the secondary payer ({classification.Scenario}); primary payer " +
                $"{classification.PrimaryPayerName ?? "unknown"}; the 837 carries no complete prior-payer " +
                $"data (2000B SBR01 and a 2320 loop with AMT*D or 2430 SVD for every earlier payer), so the " +
                $"secondary calculation cannot run. Reason code: {SecondaryNotSupportedPendReason}."),
            PendedAt = DateTime.UtcNow,
            EditFailures = new List<NcciEditFailureSnapshot>(),
        };

        return BuildModeDrivenSecondaryResult(activity);
    }

    /// <summary>
    /// Decision 8 — invoke the engine's <see cref="IPayerOrderService"/>
    /// for audit-trail rule labelling. Engine reliably differentiates
    /// Medicare scenarios; for commercial-primary cases the engine falls
    /// through to <see cref="PayerOrderRule.ExplicitCoverageRecord"/>
    /// (Phase 1 InsuredInfo has no birthday / employment data), which
    /// the stage keeps — <c>CoverageSequence="P"</c> IS the explicit
    /// signal.
    /// </summary>
    private PayerOrderRule ResolveAppliedRule(
        ClaimAdjudicationContext context,
        IReadOnlyList<CobEntry> entries)
    {
        try
        {
            var choInsured = BuildChoInsuredInfo(context);
            var allCoverages = new List<InsuredInfo>(entries.Count + 1) { choInsured };
            allCoverages.AddRange(entries.Select(MapToInsuredInfo));

            var engineResult = _payerOrder.DetermineOrder(choInsured, allCoverages);

            // Trust the engine ONLY when it confirms CHO is secondary
            // (Medicare branch path). Otherwise the engine fell through
            // to the default rule because Phase 1 InsuredInfo lacks the
            // signals it needs — record ExplicitCoverageRecord since the
            // CoverageSequence string IS the explicit determination.
            return engineResult.PayerSequence == PayerSequenceCode.Secondary
                ? engineResult.Rule
                : PayerOrderRule.ExplicitCoverageRecord;
        }
        catch (Exception ex)
        {
            // Decision 12 — engine exception caught at the stage; the
            // rule defaults to ExplicitCoverageRecord so audit trail
            // still records why CHO is secondary (the wire signal),
            // even if engine introspection failed.
            _logger.LogWarning(ex,
                "PayerOrderService threw while labelling COB rule for claim {ClaimVersionId}; defaulting to ExplicitCoverageRecord",
                SanitizeForLog(context.ClaimVersionId));
            return PayerOrderRule.ExplicitCoverageRecord;
        }
    }

    private static InsuredInfo BuildChoInsuredInfo(ClaimAdjudicationContext context)
    {
        var member = context.ResolvedMember;
        return new InsuredInfo
        {
            MemberId = context.Claim.MemberId,
            PayerId = ChoPayerIdSentinel,
            PolicyholderBirthDate = ToDateOnly(member?.DateOfBirth),
            CoverageEffectiveDate = ToDateOnly(member?.EffectiveDate),
            IsActiveEmployee = false,
            IsMedicare = false,
            MedicareDesignatedPrimary = false,
            IsLargeGroupHealthPlan = false,
        };
    }

    /// <summary>
    /// Wire <see cref="CobEntry"/> → engine <see cref="InsuredInfo"/>.
    /// Phase 1 mapping per ratified Decision 16a:
    /// <c>MedicareDesignatedPrimary = IsMedicare AND CoverageSequence="P"</c>.
    /// All other Medicare-MSP signals (LGHP, ActiveEmployee,
    /// PolicyholderBirthDate) default false / null since
    /// <c>CobEntryResponse</c> has no source for them.
    /// </summary>
    internal static InsuredInfo MapToInsuredInfo(CobEntry entry) => new()
    {
        // PayerId is populated from PolicyNumber upstream (Phase 2
        // contract — see CobEntry remarks); use it as the engine's
        // member-id key so two distinct other-coverages don't collide.
        MemberId = string.IsNullOrEmpty(entry.PolicyNumber) ? entry.PayerId : entry.PolicyNumber,
        PayerId = entry.PayerId,
        PolicyholderBirthDate = null,
        CoverageEffectiveDate = ToDateOnly(entry.CoverageBeginDate),
        IsActiveEmployee = false,
        IsMedicare = entry.IsMedicare,
        MedicareDesignatedPrimary = entry.IsMedicare && entry.IsPrimary,
        IsLargeGroupHealthPlan = false,
    };

    private static DateOnly? ToDateOnly(DateTime? value) =>
        value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static DateOnly ToDateOnly(DateTime value) =>
        DateOnly.FromDateTime(value);

    private ClaimAdjudicationStageResult BuildModeDrivenSecondaryResult(Activity? activity)
    {
        switch (_options.CobMode)
        {
            case CobEnforcementMode.Deny:
                activity?.SetTag("cob.outcome_mode", "deny");
                return ClaimAdjudicationStageResult.Deny(
                    StageName,
                    "denied for CHO-secondary scenario: the 837 carries no usable prior-payer data (Deny mode)");

            // SoftValidation used to pass here, pricing a claim this plan is
            // not primary on as primary. A later-payer claim without usable
            // COB data is never paid: it pends in SoftValidation too.
            case CobEnforcementMode.SoftValidation:
            case CobEnforcementMode.PendForSecondary:
            default:
                activity?.SetTag("cob.outcome_mode", "pend");
                return ClaimAdjudicationStageResult.Pend(
                    StageName,
                    "pended for CHO-secondary scenario: the 837 carries no usable prior-payer data; approval needs an examiner-confirmed payer order");
        }
    }

    private ClaimAdjudicationStageResult BuildDegradedOutcome(
        ClaimAdjudicationContext context, Activity? activity, bool forcePend = false)
    {
        // Decision 7 — coverage-service unavailable always pends, never
        // denies; "unable to determine coverage state" is not a denial.
        activity?.SetTag("cob.coverage_service", "unavailable");
        activity?.SetTag("cob.outcome", "degraded");

        context.CobResult = new CobOutcome
        {
            Scenario = CobScenario.None,
            PendReason = CoverageServiceUnavailablePendReason,
        };

        // Defect B fix — same audit-trail posture as BuildSecondaryOutcome:
        // record the snapshot regardless of mode.
        context.PendDetails = new PendDetails
        {
            PendCode = CobPendCode,
            PendReason = TruncatePendReason(
                $"Coverage-service unavailable; unable to determine payer order for COB. " +
                $"Reason code: {CoverageServiceUnavailablePendReason}."),
            PendedAt = DateTime.UtcNow,
            EditFailures = new List<NcciEditFailureSnapshot>(),
        };

        if (_options.CobMode == CobEnforcementMode.SoftValidation && !forcePend)
        {
            activity?.SetTag("cob.outcome_mode", "softvalidation");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        activity?.SetTag("cob.outcome_mode", "pend");
        return ClaimAdjudicationStageResult.Pend(
            StageName,
            "pended pending coverage-service availability");
    }

    private static string ScenarioTag(CobScenario scenario) => scenario switch
    {
        CobScenario.ChoPrimaryNoSecondary => "cho_primary_no_secondary",
        CobScenario.ChoPrimaryWithSecondary => "cho_primary_with_secondary",
        CobScenario.ChoSecondaryDetected => "cho_secondary_detected",
        CobScenario.ChoTertiaryDetected => "cho_tertiary_detected",
        _ => "none",
    };

    /// <summary>
    /// Earliest non-default service date across the claim header and every
    /// line — the most-restrictive interpretation. Coverage-service returns
    /// COB entries active on the supplied date; using the earliest service
    /// date catches mid-claim COB transitions on multi-line claims. Falls
    /// back to <see cref="DateTime.UtcNow"/> only when ALL dates are
    /// missing/default (unlike the naive seeded-from-header approach which
    /// would falsely fall back when the header is default but lines have
    /// real dates — Copilot review #737/4).
    /// </summary>
    internal static DateTime ResolveEarliestServiceDate(ClaimsService.Models.AdapterClaim claim)
    {
        DateTime? earliest = claim.ServiceDateFrom == default
            ? null
            : claim.ServiceDateFrom;

        foreach (var line in claim.ClaimLines)
        {
            if (line.ServiceDateFrom == default) continue;
            if (earliest is null || line.ServiceDateFrom < earliest)
            {
                earliest = line.ServiceDateFrom;
            }
        }

        return earliest ?? DateTime.UtcNow;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private static string? TruncatePendReason(string? reason)
    {
        if (reason is null) return null;
        // PendDetails.PendReason has [StringLength(500)] — mirrors
        // NcciEditsStage.TruncatePendReason.
        return reason.Length <= 500 ? reason : reason.Substring(0, 500);
    }

    /// <summary>Internal classification result threaded through the
    /// stage's outcome builders. Public for the test project via
    /// InternalsVisibleTo.</summary>
    internal sealed record ScenarioClassification(
        CobScenario Scenario,
        bool IsMedicarePrimary,
        string? PrimaryPayerName,
        string? PrimaryPayerId);
}
