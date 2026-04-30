namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.5 stub for the network &amp; credentialing enforcement stage.
/// Capability 5.6 replaces this with the real implementation that consults
/// provider-service for network membership + credentialing status, and
/// resolves the <see cref="CloudHealthOffice.BenefitEngine.Domain.NetworkTier"/>
/// the BenefitCalculationStage consumes.
///
/// <para>
/// While this stub is in place the BenefitCalculationStage falls back to
/// <see cref="CloudHealthOffice.BenefitEngine.Domain.NetworkTier.InNetwork"/>;
/// see the comment in <see cref="BenefitCalculationStage"/>.
/// </para>
/// </summary>
public sealed class NetworkCredentialingStubStage : IClaimAdjudicationStage
{
    public const string StageName = "NetworkCredentialing";

    public string Name => StageName;
    public int Order => 200;
    public bool IsRequired => false;

    public Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        return Task.FromResult(ClaimAdjudicationStageResult.Pass(StageName));
    }
}
