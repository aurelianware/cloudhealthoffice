using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication.PlanEdits;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanEdits.Contracts;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// The health plan's own edits (ADR 019): CHO sends the claim to the plan's
/// edit service — the plan's code, in the plan's repository, deployed by the
/// plan — and pends the claim with every edit that fired, for an examiner to
/// uphold or override like any CHO edit. Runs at <see cref="Order"/> = 450,
/// after CHO's own NCCI/MUE edits (400) and before coordination of benefits
/// (500).
///
/// <list type="bullet">
///   <item><description><b>No plan edit service</b> for the tenant → Pass, nothing sent.</description></item>
///   <item><description><b>No edits fired</b> → Pass.</description></item>
///   <item><description><b>Edits fired</b>, mode <see cref="PlanEditEnforcementMode.PendForReview"/>
///     → Pend with pend code <see cref="PlanEditPendCode"/> and the edits on
///     <see cref="PendDetails.PlanEditFindings"/>. An earlier stage's pend stays
///     the claim's pend code and this one is added, as NCCI does.</description></item>
///   <item><description><b>Edits fired</b>, mode <see cref="PlanEditEnforcementMode.SoftValidation"/>
///     → Pass, the edits named in the stage notes and telemetry only.</description></item>
///   <item><description><b>No usable answer</b> (timeout, unreachable, non-200,
///     a response that breaks the contract) → Pend SVCUNAVAIL, a transient pend
///     an examiner approval never overrides — or, when the plan configured
///     <see cref="PlanEditUnavailableMode.Pass"/>, Pass with a note that the
///     plan's edits were skipped.</description></item>
/// </list>
///
/// <para>
/// <b>Guardrails.</b> A plan edit can only add review: it pends, and an
/// upheld denial edit denies the lines it names (group CO, the plan's CARC).
/// It never approves, never pays, never removes a CHO edit and never denies a
/// whole claim on its own. The override policy (<see cref="EditOverridePolicy.PlanEdit"/>)
/// governs who may override it.
/// </para>
///
/// <para>
/// <b>Approval re-run.</b> The plan's service is asked again
/// (<see cref="PlanEditPurpose.ApprovalReadjudication"/>). Edits the examiner
/// decided — matched by rule and lines (<see cref="EditIdentity"/>) — are
/// settled; an edit nobody decided pends and the approval is refused.
/// </para>
/// </summary>
public sealed class PlanEditsStage : IClaimAdjudicationStage
{
    public const string StageName = "PlanEdits";

    /// <summary>Pend code of a claim pended by the plan's own edits.</summary>
    public const string PlanEditPendCode = "PLANEDIT";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly IPlanEditServiceClient _client;
    private readonly ILogger<PlanEditsStage> _logger;

    public PlanEditsStage(IPlanEditServiceClient client, ILogger<PlanEditsStage> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 450;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("Adjudication.PlanEdits", ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);

        var tenant = _client.For(context.TenantId);
        if (tenant is null)
        {
            activity?.SetTag("plan_edits.outcome", "not_configured");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }
        activity?.SetTag("plan_edits.mode", tenant.Mode.ToString());

        var request = PlanEditRequestMapper.Map(context, Guid.NewGuid().ToString("N"));
        var lineNumbers = context.Claim.ClaimLines.Select(l => l.LineNumber).ToHashSet();
        var answer = await _client.EvaluateAsync(context.TenantId, request, lineNumbers, ct).ConfigureAwait(false);

        if (answer is PlanEditCallResult.Unavailable unavailable)
        {
            activity?.SetTag("plan_edits.unavailable", unavailable.Kind);
            return Unavailable(context, tenant, unavailable, activity);
        }

        var edits = ((PlanEditCallResult.Answered)answer).Edits
            .Select(ToSnapshot)
            .DistinctBy(e => e.EditKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        activity?.SetTag("plan_edits.fired", edits.Count);
        if (edits.Count == 0)
        {
            activity?.SetTag("plan_edits.outcome", "approve");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var (undecided, decidedNotes) = SplitDecided(context, edits);
        if (undecided.Count == 0)
        {
            activity?.SetTag("plan_edits.outcome", "decided");
            return new ClaimAdjudicationStageResult { StageName = StageName, Notes = decidedNotes };
        }

        if (tenant.Mode == PlanEditEnforcementMode.SoftValidation)
        {
            activity?.SetTag("plan_edits.outcome", "softvalidation");
            return new ClaimAdjudicationStageResult
            {
                StageName = StageName,
                Notes = [.. decidedNotes, .. undecided.Select(e => $"Plan edit {e.RuleId} fired (soft validation; not pended): {e.EditKey}")],
            };
        }

        var reason = PendReason(undecided);
        RecordPend(context, reason, undecided);
        _logger.LogInformation(
            "PlanEditsStage pended claim {ClaimVersionId} with {Count} plan edit(s): {RuleIds}",
            LogSanitizer.Sanitize(context.ClaimVersionId), undecided.Count,
            LogSanitizer.Sanitize(string.Join(",", undecided.Select(e => e.RuleId))));
        activity?.SetTag("plan_edits.outcome", "pend");

        var pend = ClaimAdjudicationStageResult.Pend(StageName, "pended for the plan's edit review");
        return decidedNotes.Count == 0 ? pend : new ClaimAdjudicationStageResult
        {
            StageName = pend.StageName,
            Continue = pend.Continue,
            Outcome = pend.Outcome,
            Reason = pend.Reason,
            Notes = decidedNotes,
        };
    }

    private static ClaimAdjudicationStageResult Unavailable(
        ClaimAdjudicationContext context, PlanEditTenantOptions tenant, PlanEditCallResult.Unavailable unavailable, Activity? activity)
    {
        if (tenant.WhenUnavailable == PlanEditUnavailableMode.Pass)
        {
            activity?.SetTag("plan_edits.outcome", "skipped");
            return new ClaimAdjudicationStageResult
            {
                StageName = StageName,
                Notes = [$"Plan edits skipped: the plan edit service gave no usable answer ({unavailable.Kind}); the plan configured WhenUnavailable=Pass"],
            };
        }

        activity?.SetTag("plan_edits.outcome", "pend_unavailable");
        var reason = $"{ClaimAdjudicationOrchestrator.ServiceUnavailableReason}: plan edit service {unavailable.Kind}; " +
                     "retried on an examiner approval re-run";
        ClaimAdjudicationOrchestrator.RecordServiceUnavailablePend(context, reason);
        return ClaimAdjudicationStageResult.Pend(StageName, reason).AsCheckUnavailable(context);
    }

    /// <summary>
    /// Separates the edits a reviewer decided from the ones nobody has seen,
    /// recording the decided ones for the audit (as <see cref="NcciEditsStage"/> does).
    /// </summary>
    private static (List<PlanEditFindingSnapshot> Undecided, List<string> Notes) SplitDecided(
        ClaimAdjudicationContext context, IReadOnlyList<PlanEditFindingSnapshot> edits)
    {
        var undecided = new List<PlanEditFindingSnapshot>();
        var notes = new List<string>();
        foreach (var edit in edits)
        {
            var disposition = EditDispositionApplier.DispositionFor(context, edit.EditKey);
            if (disposition is null)
            {
                undecided.Add(edit);
                continue;
            }

            var note = disposition.Decision == EditDecision.Override
                ? $"Plan edit {edit.RuleId} overridden by examiner: {edit.EditKey}" + (string.IsNullOrWhiteSpace(disposition.Reason) ? "" : $" ({disposition.Reason})")
                : $"Plan edit {edit.RuleId} upheld by examiner{(disposition.UpheldByDefault ? " at Finish" : "")}: {edit.EditKey}";
            notes.Add(note);
            context.ExaminerOverrides.Add($"{StageName}: {note}");
        }
        return (undecided, notes);
    }

    /// <summary>
    /// Records the pend. An earlier stage's pend stays the claim's pend code
    /// (it routes the work queue) and this one is added to its additional
    /// reasons with its edits, so no reason is lost.
    /// </summary>
    private static void RecordPend(ClaimAdjudicationContext context, string reason, List<PlanEditFindingSnapshot> edits)
    {
        context.PlanEditFindings.AddRange(edits);
        if (context.PendDetails is { } existing && !string.IsNullOrWhiteSpace(existing.PendCode))
        {
            var entry = $"{PlanEditPendCode}: {reason}";
            existing.AdditionalPendReasons.Add(entry.Length > 500 ? entry[..500] : entry);
            existing.PlanEditFindings = edits.ToList();
            return;
        }

        context.PendDetails = new PendDetails
        {
            PendCode = PlanEditPendCode,
            PendReason = reason,
            PendedAt = DateTime.UtcNow,
            PlanEditFindings = edits.ToList(),
        };
    }

    internal static string PendReason(IReadOnlyList<PlanEditFindingSnapshot> edits)
    {
        var first = edits[0];
        var reason = edits.Count == 1
            ? $"Plan edit {first.RuleId}: {first.Message}"
            : $"{edits.Count} plan edits; first: {first.RuleId} {first.Message}";
        return reason.Length <= 500 ? reason : reason[..500];
    }

    internal static PlanEditFindingSnapshot ToSnapshot(PlanEdit edit)
    {
        var snapshot = new PlanEditFindingSnapshot
        {
            RuleId = edit.RuleId!,
            Message = edit.Message,
            LineNumbers = (edit.LineNumbers ?? []).Distinct().Order().ToList(),
            SuggestedCarc = edit.SuggestedCarc,
            SuggestedRarc = edit.SuggestedRarc,
        };
        snapshot.EditKey = EditIdentity.Key(snapshot);
        return snapshot;
    }
}
