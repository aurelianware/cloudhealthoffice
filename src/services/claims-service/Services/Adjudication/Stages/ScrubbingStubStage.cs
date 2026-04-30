namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.5 stub for the pre-adjudication scrubbing stage.
/// Capability 5.4 replaces this with a real <c>ClaimsScrubEngine</c>-backed
/// implementation via DI swap (same <see cref="IClaimAdjudicationStage.Name"/>,
/// same <see cref="IClaimAdjudicationStage.Order"/>); the swap doesn't
/// touch the orchestrator or any other stage.
/// </summary>
public sealed class ScrubbingStubStage : IClaimAdjudicationStage
{
    public const string StageName = "Scrubbing";

    public string Name => StageName;
    public int Order => 100;
    public bool IsRequired => false;

    public Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        return Task.FromResult(ClaimAdjudicationStageResult.Pass(StageName));
    }
}
