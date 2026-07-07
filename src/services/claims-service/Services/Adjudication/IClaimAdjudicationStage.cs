namespace ClaimsService.Services.Adjudication;

/// <summary>
/// A single stage in the claim adjudication pipeline (capability 5.5).
/// Stages are registered as <c>IEnumerable&lt;IClaimAdjudicationStage&gt;</c>;
/// the orchestrator iterates them in <see cref="Order"/> ascending order
/// and invokes <see cref="ExecuteAsync"/> on each enabled stage.
///
/// <para>
/// Stage ordering is platform-level architecture (NCCI before COB, COB
/// before persistence, etc.) and is not configurable per-tenant. Tenant
/// configuration controls only <em>whether</em> a stage runs, via
/// <see cref="Models.Adjudication.AdjudicationPipelineOptions.EnabledStages"/>.
/// </para>
///
/// <para>
/// 5.5 ships two real stages — <see cref="Stages.BenefitCalculationStage"/>
/// and <see cref="Stages.PersistenceStage"/> — plus five stub stages that
/// capabilities 5.4, 5.6, 5.7, 5.8, and 5.9 replace via DI swap.
/// </para>
/// </summary>
public interface IClaimAdjudicationStage
{
    /// <summary>
    /// Stable identifier — used both for telemetry tags and as the key
    /// into <see cref="Models.Adjudication.AdjudicationPipelineOptions.EnabledStages"/>.
    /// Stage names use PascalCase (e.g. <c>"Scrubbing"</c>,
    /// <c>"Persistence"</c>); the EnabledStages dictionary is built with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> so config-side
    /// casing variants resolve correctly. Stub-stage replacements
    /// (5.4-5.9) MUST keep the same Name so the per-tenant enablement
    /// config keeps working unchanged.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Position in the pipeline. Conventional ordering:
    /// 100 Scrubbing, 200 NetworkCredentialing, 300 BenefitCalculation,
    /// 400 NcciEdits, 500 CoordinationOfBenefits, 600 AiExamination,
    /// 999 Persistence.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// When true, the stage runs even if explicitly disabled in
    /// <see cref="Models.Adjudication.AdjudicationPipelineOptions"/>.
    /// Currently only <see cref="Stages.PersistenceStage"/> is required —
    /// without it, the version chain wouldn't capture the adjudication
    /// outcome at all.
    /// </summary>
    bool IsRequired { get; }

    Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct);
}

/// <summary>
/// The result a stage hands back to the orchestrator. <see cref="Continue"/>
/// false short-circuits the remainder of the pipeline (everything before
/// the persistence stage); <see cref="Stages.PersistenceStage"/> always
/// runs regardless because the outcome of every stage — pass, pend, or
/// terminal failure — must be captured on the claim version.
/// </summary>
public class ClaimAdjudicationStageResult
{
    public required string StageName { get; init; }

    /// <summary>
    /// False = short-circuit to <see cref="Stages.PersistenceStage"/>; the
    /// remaining non-persistence stages are skipped.
    /// </summary>
    public bool Continue { get; init; } = true;

    public ClaimAdjudicationOutcome Outcome { get; init; } = ClaimAdjudicationOutcome.Pass;

    public string? Reason { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public static ClaimAdjudicationStageResult Pass(string stageName) =>
        new() { StageName = stageName, Continue = true, Outcome = ClaimAdjudicationOutcome.Pass };

    public static ClaimAdjudicationStageResult Pend(string stageName, string reason) =>
        new()
        {
            StageName = stageName,
            Continue = true,
            Outcome = ClaimAdjudicationOutcome.Pend,
            Reason = reason
        };

    public static ClaimAdjudicationStageResult Reject(string stageName, string reason) =>
        new()
        {
            StageName = stageName,
            Continue = false,
            Outcome = ClaimAdjudicationOutcome.Reject,
            Reason = reason
        };

    public static ClaimAdjudicationStageResult Deny(string stageName, string reason) =>
        new()
        {
            StageName = stageName,
            Continue = false,
            Outcome = ClaimAdjudicationOutcome.Deny,
            Reason = reason
        };

    /// <summary>
    /// Precedence rule for combining every stage's result into one
    /// adjudication outcome: Reject &gt; Deny &gt; Pend &gt; Pass. Shared by
    /// two callers that need it at two different points in the pipeline:
    /// <see cref="Stages.PersistenceStage"/> calls this with
    /// <c>context.StageResults</c> as they stand BEFORE Persistence's own
    /// result is appended (Persistence is always <c>Order=999</c>, i.e.
    /// last) to decide whether the orchestrator computed a Pend and
    /// therefore whether to persist <c>ClaimStatus.Pended</c>; the
    /// orchestrator's own final-outcome resolution (used to label the
    /// emitted Service Bus event) calls this AFTER every stage including
    /// Persistence has run, so a persistence failure (Reject) still wins.
    /// </summary>
    public static ClaimAdjudicationOutcome ResolveOutcome(IReadOnlyList<ClaimAdjudicationStageResult> results)
    {
        if (results.Any(r => r.Outcome == ClaimAdjudicationOutcome.Reject))
            return ClaimAdjudicationOutcome.Reject;
        if (results.Any(r => r.Outcome == ClaimAdjudicationOutcome.Deny))
            return ClaimAdjudicationOutcome.Deny;
        if (results.Any(r => r.Outcome == ClaimAdjudicationOutcome.Pend))
            return ClaimAdjudicationOutcome.Pend;
        return ClaimAdjudicationOutcome.Pass;
    }
}

/// <summary>
/// Discriminator for stage outcomes.
/// <list type="bullet">
///   <item><description><see cref="Pass"/> — stage approved, pipeline continues.</description></item>
///   <item><description><see cref="Pend"/> — recoverable; pipeline continues so subsequent stages can decorate the result, then the claim ends up in a human-review queue.</description></item>
///   <item><description><see cref="Reject"/> — terminal pre-adjudication failure (e.g., scrubbing). Subsequent stages are skipped; PersistenceStage records the rejection.</description></item>
///   <item><description><see cref="Deny"/> — terminal benefit-side denial (e.g., not covered). Subsequent stages are skipped; PersistenceStage records the denial.</description></item>
/// </list>
/// </summary>
public enum ClaimAdjudicationOutcome
{
    Pass = 0,
    Pend = 1,
    Reject = 2,
    Deny = 3
}
