namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.5 stub for the NCCI edits stage. Capability 5.7 replaces
/// this with a real <c>NcciEngine</c>-backed implementation. Same
/// <see cref="IClaimAdjudicationStage.Name"/> + <see cref="IClaimAdjudicationStage.Order"/>
/// so disabling via configuration keeps working across the swap.
/// </summary>
public sealed class NcciEditsStubStage : IClaimAdjudicationStage
{
    public const string StageName = "NcciEdits";

    public string Name => StageName;
    public int Order => 400;
    public bool IsRequired => false;

    public Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        return Task.FromResult(ClaimAdjudicationStageResult.Pass(StageName));
    }
}
