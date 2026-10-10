using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Commits the claim's accumulator updates — once, and only for a claim
/// whose adjudication passed.
///
/// <para>
/// <see cref="BenefitCalculationStage"/> (Order 300) prices every claim
/// read-only (<c>Prospective</c>) and the engine hands back the write it
/// would make (<see cref="BenefitResolutionResult.PreparedAccumulatorCommit"/>).
/// Before, the stage priced in Production whenever no earlier stage had
/// pended, so a claim pended later — NCCI at 400, AI review at 600 — or
/// denied later had already written deductible / OOP / visit counts that
/// stayed on the member until an examiner acted (H4).
/// </para>
///
/// <para>
/// Runs at Order 990: after every review stage, before
/// <see cref="PersistenceStage"/>. It commits only when every stage so far
/// passed. It is required (runs after a short-circuit and its exceptions are
/// not turned into a Reject): a commit that fails throws, the Service Bus
/// message is redelivered and the run is repeated — the projection was not
/// written yet, so the redelivery re-adjudicates, and the commit, keyed on
/// the claim, replaces whatever an earlier attempt wrote.
/// </para>
///
/// <para>
/// An examiner-approval re-run does not commit here. Its commit is made by
/// the resolver after its final write, fenced on the resolution lock, has
/// landed (<c>ClaimsController.ResolvePendedClaim</c>): a re-run whose lock
/// expires mid-run, and whose claim another examiner then denies, never
/// commits — and if its commit were still in flight, the denial's terminal
/// reversal fences the claim id in the accumulator store so the commit is
/// refused there.
/// </para>
/// </summary>
public sealed class AccumulatorCommitStage : IClaimAdjudicationStage
{
    public const string StageName = "AccumulatorCommit";

    private readonly IBenefitCalculationEngine _engine;
    private readonly ILogger<AccumulatorCommitStage> _logger;

    public AccumulatorCommitStage(IBenefitCalculationEngine engine, ILogger<AccumulatorCommitStage> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 990;
    public bool IsRequired => true;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        var outcome = ClaimAdjudicationStageResult.ResolveOutcome(context.StageResults);
        if (outcome != ClaimAdjudicationOutcome.Pass)
            return Note($"Not committed: the claim's adjudication is {outcome}; accumulators are written only when it passes.");

        var benefit = context.BenefitResolutionResult;
        if (benefit is null)
            return Note("Nothing to commit: benefit calculation did not run.");

        var commit = benefit.PreparedAccumulatorCommit;
        if (commit is null)
        {
            // The engine priced the claim read-only but returned no prepared
            // write: a benefit-plan-service build older than this one. Never
            // pass a claim without its accumulators — fail the run so the
            // message is retried once benefit-plan-service is upgraded.
            throw new InvalidOperationException(
                $"Benefit calculation for claim {context.Claim.Id} returned no prepared accumulator commit; " +
                "benefit-plan-service must be upgraded before claims-service (see the deploy notes).");
        }

        if (context.ExaminerApproval is not null)
            return Note("Commit deferred to the examiner resolution, after its lock-fenced final write.");

        var result = await _engine.CommitAccumulatorsAsync(commit, ct).ConfigureAwait(false);
        if (result == AccumulatorCommitOutcome.RefusedClaimReversed)
        {
            _logger.LogWarning(
                "Accumulator commit for claim {ClaimId} refused: the claim was voided or denied (reversed terminally)",
                SanitizeForLog(context.Claim.Id));
            return ClaimAdjudicationStageResult.Reject(
                StageName,
                "The claim's accumulators were reversed terminally (voided or denied); this adjudication was not applied.");
        }

        _logger.LogInformation(
            "Committed accumulators for claim {ClaimId} ({Outcome}, commit {CommitId})",
            SanitizeForLog(context.Claim.Id), result, SanitizeForLog(commit.CommitId));
        return Note($"Accumulators committed ({result}).");
    }

    private static ClaimAdjudicationStageResult Note(string note) => new()
    {
        StageName = StageName,
        Outcome = ClaimAdjudicationOutcome.Pass,
        Notes = [note],
    };

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
