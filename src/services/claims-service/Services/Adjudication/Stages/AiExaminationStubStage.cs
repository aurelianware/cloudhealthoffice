namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.5 stub for the AI-backed examination stage. Capability 5.9
/// replaces this with a real implementation that consults
/// claims-examiner-service. 5.9 may revisit synchronous-vs-async semantics
/// — if AI examination needs to run async via a separate subscription, the
/// stub stays in place and 5.9 wires the async path elsewhere. The stub is
/// non-blocking either way.
/// </summary>
public sealed class AiExaminationStubStage : IClaimAdjudicationStage
{
    public const string StageName = "AiExamination";

    public string Name => StageName;
    public int Order => 600;
    public bool IsRequired => false;

    public Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        return Task.FromResult(ClaimAdjudicationStageResult.Pass(StageName));
    }
}
