using System.Diagnostics;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication.Mapping;
using Microsoft.Extensions.Options;
using EngineModels = CloudHealthOffice.NcciEngine.Models;
using EngineServices = CloudHealthOffice.NcciEngine.Services;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.7 — replaces <see cref="NcciEditsStubStage"/>. Drives every
/// adjudicated claim through <c>CloudHealthOffice.NcciEngine</c> for NCCI
/// Column 1/Column 2 (PTP) bundling edits and MUE (Medically Unlikely
/// Edits) unit checks. Runs at <see cref="Order"/> = 400 — between
/// BenefitCalculationStage (300) and CoB (500) — so post-edit
/// adjustments to allowed amount are computable downstream (deferred
/// to capability 5.10 Remittance Generation).
///
/// <list type="bullet">
///   <item><description>Engine returns clean → <see cref="ClaimAdjudicationStageResult.Pass"/>;
///     pipeline continues normally.</description></item>
///   <item><description>Engine returns failures and tenant <see cref="NcciEnforcementMode"/>
///     is <see cref="NcciEnforcementMode.PendForReview"/> (default) →
///     <c>Pend</c>; pipeline continues so subsequent stages can decorate.
///     <see cref="ClaimAdjudicationContext.PendDetails"/> carries the
///     deterministic snapshot through to the projection-bypass write.</description></item>
///   <item><description>Engine returns failures and tenant mode is
///     <see cref="NcciEnforcementMode.Deny"/> → <c>Deny</c>; pipeline
///     short-circuits to PersistenceStage (Reject is reserved for
///     structural pre-adjudication failures from 5.4).</description></item>
///   <item><description>Engine returns failures and tenant mode is
///     <see cref="NcciEnforcementMode.SoftValidation"/> → <c>Pass</c>;
///     failures still recorded on <see cref="ClaimAdjudicationContext.PendDetails"/>
///     for telemetry and downstream visibility but no payment effect.</description></item>
///   <item><description>Engine throws → synthetic <c>ENGINE_EXCEPTION</c>
///     snapshot appended, mode-driven outcome (Pend / Deny / Pass) so
///     the orchestrator's safety-net catch is a fallback rather than
///     the primary degraded path.</description></item>
///   <item><description>No valid lines after mapper filtering → soft-pass
///     with <c>MAPPER_INVALID_LINES</c> note. Engine has
///     <c>[Required] [MinLength(1)]</c> on <see cref="EngineModels.NcciScrubRequest.ServiceLines"/>
///     so calling with zero lines would throw at the boundary.</description></item>
/// </list>
///
/// <para>
/// <b>Required (Decision 3).</b> <see cref="IsRequired"/> = true. NCCI
/// is foundational claim integrity — disabling produces unreliable
/// downstream payment results (claims paid for bundled codes that
/// should have been denied). Tenants that don't want NCCI policy
/// enforcement set <c>NcciMode = SoftValidation</c> instead of
/// disabling the stage.
/// </para>
///
/// <para>
/// <b>Missing-table behavior (Decision 11).</b> When a tenant has no
/// NCCI / MUE data loaded, the engine's repository lookups return null
/// for every pair / MUE entry and the result is <c>Passed=true</c>
/// with zero failures — the soft-pass posture is the engine's natural
/// behavior. The stage does not need a pre-flight
/// <see cref="EngineServices.INcciEditService.GetTableVersionAsync"/>
/// guard; missing data simply produces a clean pass with telemetry.
/// </para>
/// </summary>
public sealed class NcciEditsStage : IClaimAdjudicationStage
{
    public const string StageName = "NcciEdits";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly EngineServices.INcciEditService _engine;
    private readonly TenantEnforcementPolicyOptions _options;
    private readonly ILogger<NcciEditsStage> _logger;

    public NcciEditsStage(
        EngineServices.INcciEditService engine,
        IOptions<TenantEnforcementPolicyOptions> options,
        ILogger<NcciEditsStage> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 400;
    public bool IsRequired => true;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity(
            "Adjudication.NcciEdits",
            ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);
        activity?.SetTag("ncci.mode", _options.NcciMode.ToString());

        var request = ClaimToNcciScrubRequestMapper.Map(context.Claim);

        if (request.ServiceLines.Count == 0)
        {
            // Engine [Required] [MinLength(1)] would throw — treat as a
            // structured soft-pass so an upstream data-quality gap does
            // not stall the pipeline. Telemetry surfaces it for ops.
            // Distinct from the engine-side missing-table soft-pass: this
            // is a mapper-side data-quality signal (e.g., line procedure
            // codes that aren't 5-char CPT/HCPCS, units out of [0.01,9999],
            // or missing service dates).
            _logger.LogInformation(
                "NcciEditsStage soft-pass for claim {ClaimVersionId}: no engine-valid lines after mapper filtering",
                SanitizeForLog(context.ClaimVersionId));
            activity?.SetTag("ncci.outcome", "softpass");
            activity?.SetTag("ncci.engine_status", "mapper_invalid_lines");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        EngineModels.NcciScrubResult engineResult;
        try
        {
            engineResult = await _engine.ScrubAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "NCCI engine threw for claim {ClaimVersionId}; degrading to mode-driven outcome",
                SanitizeForLog(context.ClaimVersionId));
            activity?.SetTag("ncci.engine_status", "exception");

            ApplyEngineExceptionSnapshot(context, ex);
            return BuildModeDrivenResult(activity, hasFailures: true);
        }

        activity?.SetTag("ncci.engine_status", "success");
        activity?.SetTag("ncci.pairs_checked", engineResult.NcciPairsChecked);
        activity?.SetTag("ncci.mues_checked", engineResult.MueChecked);
        activity?.SetTag("ncci.failures", engineResult.EditFailures.Count);

        if (engineResult.EditFailures.Count == 0)
        {
            activity?.SetTag("ncci.outcome", "approve");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        ApplyFailureSnapshots(context, engineResult.EditFailures);
        return BuildModeDrivenResult(activity, hasFailures: true);
    }

    private void ApplyFailureSnapshots(
        ClaimAdjudicationContext context,
        IReadOnlyList<EngineModels.NcciEditFailure> failures)
    {
        var snapshots = failures.Select(MapFailure).ToList();
        var pendCode = ResolvePendCode(failures);
        var firstFailure = snapshots[0];
        var pendReason = snapshots.Count == 1
            ? firstFailure.Message
            : $"{snapshots.Count} NCCI/MUE edit failures; first: {firstFailure.RuleId} {firstFailure.Message}";

        context.PendDetails = new PendDetails
        {
            PendCode = pendCode,
            PendReason = TruncatePendReason(pendReason),
            PendedAt = DateTime.UtcNow,
            EditFailures = snapshots,
        };

        _logger.LogInformation(
            "NcciEditsStage recorded {Count} edit failure(s) on claim {ClaimVersionId} (mode={Mode}, pendCode={PendCode})",
            snapshots.Count,
            SanitizeForLog(context.ClaimVersionId),
            _options.NcciMode,
            pendCode);
    }

    private void ApplyEngineExceptionSnapshot(ClaimAdjudicationContext context, Exception ex)
    {
        var snapshot = new NcciEditFailureSnapshot
        {
            EditType = "EngineError",
            RuleId = "ENGINE_EXCEPTION",
            Message = $"NCCI engine threw: {ex.GetType().Name}",
            AffectedLineNumbers = new List<int>(),
            ModifierOverridePresent = false,
        };

        context.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = TruncatePendReason(snapshot.Message),
            PendedAt = DateTime.UtcNow,
            EditFailures = new List<NcciEditFailureSnapshot> { snapshot },
        };
    }

    private ClaimAdjudicationStageResult BuildModeDrivenResult(Activity? activity, bool hasFailures)
    {
        switch (_options.NcciMode)
        {
            case NcciEnforcementMode.Deny:
                activity?.SetTag("ncci.outcome", "deny");
                return ClaimAdjudicationStageResult.Deny(
                    StageName,
                    BuildResultReason("denied for NCCI/MUE failure"));

            case NcciEnforcementMode.SoftValidation:
                activity?.SetTag("ncci.outcome", "softvalidation");
                return ClaimAdjudicationStageResult.Pass(StageName);

            case NcciEnforcementMode.PendForReview:
            default:
                activity?.SetTag("ncci.outcome", "pend");
                return ClaimAdjudicationStageResult.Pend(
                    StageName,
                    BuildResultReason("pended for NCCI/MUE review"));
        }
    }

    private static string BuildResultReason(string prefix) => prefix;

    /// <summary>
    /// Pend code routes the work queue. If only MUE failures fire ⇒
    /// "MUE"; otherwise "NCCI" (umbrella for pair edits or the mixed
    /// pair+MUE case). Recognized values match the work-queue
    /// categorizer comments on <see cref="PendDetails.PendCode"/>.
    /// </summary>
    private static string ResolvePendCode(IReadOnlyList<EngineModels.NcciEditFailure> failures)
    {
        if (failures.All(f => f.EditType == EngineModels.NcciEditType.Mue))
        {
            return "MUE";
        }
        return "NCCI";
    }

    /// <summary>
    /// Engine NcciEditFailure → claims-service NcciEditFailureSnapshot
    /// (Decision 13). Trivial 1:1 — engine pre-filters override-present
    /// cases, so <see cref="EngineModels.NcciEditFailure.ModifierOverridePresent"/>
    /// is structurally false on emitted failures (Decision 15).
    /// </summary>
    internal static NcciEditFailureSnapshot MapFailure(EngineModels.NcciEditFailure failure) => new()
    {
        EditType = MapEditType(failure.EditType),
        RuleId = failure.RuleId,
        Message = failure.Message,
        Column1Code = failure.Column1Code,
        Column2Code = failure.Column2Code,
        AffectedLineNumbers = failure.AffectedLineNumbers?.ToList() ?? new List<int>(),
        ModifierOverridePresent = failure.ModifierOverridePresent,
        UnitsBilled = failure.UnitsBilled,
        MueMaxUnits = failure.MueMaxUnits,
        SuggestedCarc = failure.SuggestedCarc,
        SuggestedRarc = failure.SuggestedRarc,
    };

    /// <summary>
    /// Engine enum → snapshot string. The snapshot's
    /// <see cref="NcciEditFailureSnapshot.IsModifierAddressable"/> uses
    /// case-insensitive comparison against "NcciPair" (Decision 14).
    /// </summary>
    internal static string MapEditType(EngineModels.NcciEditType editType) => editType switch
    {
        EngineModels.NcciEditType.NcciPair => "NcciPair",
        EngineModels.NcciEditType.Mue => "Mue",
        _ => "Unknown",
    };

    private static string? TruncatePendReason(string? reason)
    {
        if (reason is null) return null;
        // PendDetails.PendReason has [StringLength(500)]
        return reason.Length <= 500 ? reason : reason.Substring(0, 500);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
