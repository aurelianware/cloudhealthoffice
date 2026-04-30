namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.5 stub for the Coordination of Benefits (COB) stage.
/// Capability 5.8 replaces this with a real <c>CobEngine</c>-backed
/// implementation that decides primary / secondary / tertiary payer
/// responsibility and adjusts the <see cref="ClaimAdjudicationContext.AdjudicationResult"/>
/// accordingly.
/// </summary>
public sealed class CoordinationOfBenefitsStubStage : IClaimAdjudicationStage
{
    public const string StageName = "CoordinationOfBenefits";

    public string Name => StageName;
    public int Order => 500;
    public bool IsRequired => false;

    public Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        return Task.FromResult(ClaimAdjudicationStageResult.Pass(StageName));
    }
}
