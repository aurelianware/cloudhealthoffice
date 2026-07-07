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
/// the context. Runs at <see cref="Order"/> = 500 — between
/// <see cref="NcciEditsStage"/> (400) and AI examination (600) — so any
/// post-edit allowed-amount adjustment from 5.7 is finalized before COB
/// reasoning kicks in.
///
/// <para>
/// <b>Phase 1 detection-only posture (Decision 3).</b> The stage detects
/// CHO-secondary scenarios and produces a structured Pend with the stable
/// reason <c>cob-secondary-not-supported-phase-1</c> — Phase 1 ships
/// CHO-primary adjudication only. Phase 2 priorEob work will exercise
/// <see cref="ICobCalculationService"/> (registered but unused in 5.8) for
/// CHO-secondary calculation, lift the pend, and extend
/// <see cref="ClaimsService.Models.AdjudicationResult"/> with
/// CHO-secondary persistence fields.
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
/// — wrong on the wire. Tenants that don't want CoB gating set
/// <c>CobMode = SoftValidation</c> instead of disabling the stage; the
/// detection still happens, telemetry still fires, but the stage returns
/// Pass.
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
    public int Order => 500;
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

        if (entries is null)
        {
            // Decision 7 — Pend regardless of mode. Coverage data
            // unavailable is not a denial signal.
            return BuildDegradedOutcome(context, activity);
        }

        activity?.SetTag("cob.coverage_service", "success");
        activity?.SetTag("cob.entry_count", entries.Count);

        var classification = Classify(entries);
        activity?.SetTag("cob.outcome", ScenarioTag(classification.Scenario));

        return classification.Scenario switch
        {
            CobScenario.ChoPrimaryNoSecondary => BuildPrimaryOutcome(
                context, activity, classification),
            CobScenario.ChoPrimaryWithSecondary => BuildPrimaryOutcome(
                context, activity, classification),
            CobScenario.ChoSecondaryDetected or CobScenario.ChoTertiaryDetected =>
                BuildSecondaryOutcome(context, activity, classification, entries),
            // CobScenario.None can't be reached from Classify (only the
            // degraded path produces None) — defensive default mirrors
            // the degraded path so a future enum addition fails safe.
            _ => BuildDegradedOutcome(context, activity),
        };
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
                $"{classification.PrimaryPayerName ?? "unknown"}; secondary claim calculation " +
                $"deferred to Phase 2. Reason code: {SecondaryNotSupportedPendReason}."),
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
                    "denied for CHO-secondary scenario; secondary calculation deferred to Phase 2");

            case CobEnforcementMode.SoftValidation:
                activity?.SetTag("cob.outcome_mode", "softvalidation");
                return ClaimAdjudicationStageResult.Pass(StageName);

            case CobEnforcementMode.PendForSecondary:
            default:
                activity?.SetTag("cob.outcome_mode", "pend");
                return ClaimAdjudicationStageResult.Pend(
                    StageName,
                    "pended for CHO-secondary scenario; secondary calculation deferred to Phase 2");
        }
    }

    private ClaimAdjudicationStageResult BuildDegradedOutcome(
        ClaimAdjudicationContext context, Activity? activity)
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

        if (_options.CobMode == CobEnforcementMode.SoftValidation)
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
