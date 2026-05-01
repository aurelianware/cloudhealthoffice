using System.Diagnostics;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication.Mapping;
using EngineModels = CloudHealthOffice.ClaimsScrubEngine.Models;
using EngineServices = CloudHealthOffice.ClaimsScrubEngine.Services;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.4 — replaces <see cref="ScrubbingStubStage"/>. Drives
/// every adjudicated claim through <c>CloudHealthOffice.ClaimsScrubEngine</c>
/// for structural validation before any downstream stage runs.
///
/// <list type="bullet">
///   <item><description>Engine returns clean (zero errors, zero warnings)
///     → <see cref="ClaimAdjudicationStageResult.Pass"/>; pipeline
///     continues normally.</description></item>
///   <item><description>Engine returns warnings only → <c>Pass</c> with
///     warnings recorded on
///     <see cref="ClaimAdjudicationContext.ScrubbingResult"/>; pipeline
///     continues so warnings can decorate the final adjudication.</description></item>
///   <item><description>Engine returns at least one Error result →
///     <see cref="ClaimAdjudicationStageResult.Reject"/>; pipeline
///     short-circuits to PersistenceStage. The 277CA acknowledgment
///     surface (<c>ClaimAcknowledgmentService.Generate277CA</c>) is the
///     correct response channel for these structural failures.</description></item>
///   <item><description>Engine throws → <c>Reject</c> with structured
///     <c>ENGINE_EXCEPTION</c> violation. Mirrors 5.6's
///     <see cref="NetworkCredentialingStage"/> pattern of producing
///     structured outcomes rather than letting the orchestrator's
///     safety-net catch produce a stringly-typed Reject.</description></item>
/// </list>
///
/// <para>
/// <b>Required (Decision 4).</b> <see cref="IsRequired"/> = true. A
/// structurally invalid claim corrupts every downstream stage —
/// disabling scrubbing produces unreliable BenefitCalculation, NCCI,
/// and COB outcomes. Per-tenant disablement remains a Phase 2 surface;
/// the orchestrator treats <c>IsRequired=true</c> as non-overridable
/// (<see cref="ClaimAdjudicationOrchestrator.IsEnabled"/>).
/// </para>
/// </summary>
public sealed class ScrubbingStage : IClaimAdjudicationStage
{
    public const string StageName = "Scrubbing";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly EngineServices.IClaimRoutingService _engine;
    private readonly ILogger<ScrubbingStage> _logger;

    public ScrubbingStage(
        EngineServices.IClaimRoutingService engine,
        ILogger<ScrubbingStage> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 100;
    public bool IsRequired => true;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity(
            "Adjudication.Scrubbing",
            ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("tenant.id", context.TenantId);

        EngineModels.X12837Claim engineClaim;
        try
        {
            engineClaim = ClaimToX12837Mapper.Map(context.Claim, context.ResolvedMember);
        }
        catch (Exception ex)
        {
            // Don't put ex.Message on the audit trail — mapper exceptions
            // can wrap claim data (member ids, NPIs, dates) into the message
            // string, which would persist as PHI on the version record.
            // Full exception detail goes to ILogger only.
            _logger.LogError(ex,
                "ClaimToX12837Mapper threw for claim {ClaimVersionId}; treating as Reject",
                SanitizeForLog(context.ClaimVersionId));
            context.ScrubbingResult = BuildExceptionOutcome(
                ex, "MAPPER_EXCEPTION",
                $"Scrubbing mapper threw: {ex.GetType().Name}");
            activity?.SetTag("scrubbing.decision", ScrubbingDecision.RejectStructural.ToString());
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                $"Scrubbing mapper threw: {ex.GetType().Name}");
        }

        EngineModels.ClaimsScrubResponse response;
        try
        {
            response = await _engine.ScrubAndRouteAsync(
                new EngineModels.ClaimsScrubRequest { Claim = engineClaim },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Scrubbing engine threw for claim {ClaimVersionId}; treating as Reject",
                SanitizeForLog(context.ClaimVersionId));
            context.ScrubbingResult = BuildExceptionOutcome(
                ex, "ENGINE_EXCEPTION",
                $"Scrubbing engine threw: {ex.GetType().Name}");
            activity?.SetTag("scrubbing.decision", ScrubbingDecision.RejectStructural.ToString());
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                $"Scrubbing engine threw: {ex.GetType().Name}");
        }

        var validation = response.Result;
        var errors = ProjectViolations(validation, EngineModels.ValidationSeverity.Error);
        // Bucket Info into Warnings — engine's default rule set never
        // emits Info today, but the contract permits it; treat as
        // pass-through audit signal (same as Warning).
        var warnings = ProjectViolations(
            validation,
            EngineModels.ValidationSeverity.Warning,
            EngineModels.ValidationSeverity.Info);

        var decision = errors.Count > 0
            ? ScrubbingDecision.RejectStructural
            : ScrubbingDecision.Approve;

        context.ScrubbingResult = new ScrubbingOutcome
        {
            Decision = decision,
            Errors = errors,
            Warnings = warnings,
            RoutingNote = validation.Routing?.Reason,
            RulesExecuted = validation.RulesExecuted,
            EngineStatus = validation.Status,
        };

        activity?.SetTag("scrubbing.decision", decision.ToString());
        activity?.SetTag("scrubbing.errors", errors.Count);
        activity?.SetTag("scrubbing.warnings", warnings.Count);

        if (decision == ScrubbingDecision.RejectStructural)
        {
            var reason = errors.Count == 1
                ? $"{errors[0].RuleId}: {errors[0].Message}"
                : $"{errors.Count} structural error(s); first: {errors[0].RuleId}: {errors[0].Message}";

            _logger.LogInformation(
                "ScrubbingStage rejected claim {ClaimVersionId}: {Errors} errors, {Warnings} warnings",
                SanitizeForLog(context.ClaimVersionId), errors.Count, warnings.Count);

            return ClaimAdjudicationStageResult.Reject(StageName, reason);
        }

        if (warnings.Count > 0)
        {
            _logger.LogInformation(
                "ScrubbingStage passed claim {ClaimVersionId} with {Warnings} warnings",
                SanitizeForLog(context.ClaimVersionId), warnings.Count);
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private static IReadOnlyList<RuleViolation> ProjectViolations(
        EngineModels.ClaimValidationResult result,
        params EngineModels.ValidationSeverity[] severities)
    {
        if (result.Results is null || result.Results.Count == 0)
            return Array.Empty<RuleViolation>();

        return result.Results
            .Where(r => !r.Passed && r.Severity is { } sev && severities.Contains(sev))
            .Select(r => new RuleViolation(
                RuleId: r.RuleId,
                RuleName: r.RuleName,
                Message: r.Message ?? string.Empty,
                Field: r.Fields is { Count: > 0 } ? string.Join(",", r.Fields) : null,
                EditCode: r.EditCode,
                ServiceLines: r.ServiceLines is { Count: > 0 }
                    ? r.ServiceLines.AsReadOnly()
                    : null))
            .ToList();
    }

    private static ScrubbingOutcome BuildExceptionOutcome(Exception ex, string ruleId, string message)
        => new()
        {
            Decision = ScrubbingDecision.RejectStructural,
            Errors = new[]
            {
                new RuleViolation(
                    RuleId: ruleId,
                    RuleName: ex.GetType().Name,
                    Message: message,
                    Field: null,
                    EditCode: null,
                    ServiceLines: null),
            },
            EngineStatus = "rejected",
        };

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
