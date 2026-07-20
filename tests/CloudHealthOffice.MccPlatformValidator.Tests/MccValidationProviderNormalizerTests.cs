using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccValidationProviderNormalizerTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Theory]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_ExpiredAuth)]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_WrongProvider)]
    [InlineData(EdgeCaseScenario.PriorAuthRequired_WrongProcedure)]
    public void Normalize_IsolatesPriorAuthValidationEvidenceWhenScoringIsEnabled(EdgeCaseScenario scenario)
    {
        var claim = PriorAuthClaim(scenario);

        var normalized = MccValidationProviderNormalizer.Normalize(
            new[] { claim },
            seed: 42,
            runId: RunId,
            new MccWorkflowValidationCapabilities(
                ScorePriorAuthValidationEvidence: true,
                ScorePriorAuthProviderValidationEvidence: true));

        Assert.Equal(1, normalized);
        Assert.Equal(MccValidationProviderIdentity.BuildNpi(42, RunId, 0, role: 0), claim.BillingProvider.Npi);
        Assert.Equal(MccValidationProviderIdentity.BuildNpi(42, RunId, 0, role: 1), claim.RenderingProvider.Npi);
        AssertAdjudicatable(claim.BillingProvider);
        AssertAdjudicatable(claim.RenderingProvider);
    }

    [Fact]
    public void Normalize_LeavesPriorAuthValidationEvidenceUnsupportedWhenScoringIsDisabled()
    {
        var claim = PriorAuthClaim(EdgeCaseScenario.PriorAuthRequired_ExpiredAuth);

        var normalized = MccValidationProviderNormalizer.Normalize(
            new[] { claim },
            seed: 42,
            runId: RunId);

        Assert.Equal(0, normalized);
        Assert.Equal("1111111111", claim.BillingProvider.Npi);
        Assert.Equal("2222222222", claim.RenderingProvider.Npi);
    }

    private static SyntheticClaim PriorAuthClaim(EdgeCaseScenario scenario)
    {
        return new SyntheticClaim
        {
            ClaimId = "MCC-E-0000001",
            ClaimType = "Institutional",
            EdgeCase = scenario,
            DateOfService = new DateTime(2026, 6, 20),
            PlaceOfService = "21",
            PriorAuthStatus = scenario is EdgeCaseScenario.PriorAuthRequired_ExpiredAuth ? "Expired" : "OnFile",
            PriorAuthNumber = "AUTH-TEST",
            BillingProvider = Provider("1111111111"),
            RenderingProvider = Provider("2222222222"),
            ExpectedOutcome = new ExpectedOutcome
            {
                Disposition = "Denied",
                DenialReasonCode = "197"
            }
        };
    }

    private static SyntheticProvider Provider(string npi) => new()
    {
        Npi = npi,
        ProviderType = "Individual",
        SpecialtyCode = "207Q00000X",
        TaxonomyCode = "207Q00000X",
        IsParticipating = false,
        NetworkStatus = "Excluded",
        CredentialingStatus = "Excluded",
        State = "TX",
        ContractType = "FeeForService",
        FeeScheduleId = "FS-MEDICAID",
        EffectiveDate = new DateTime(2026, 7, 1),
        TermDate = new DateTime(2026, 7, 10),
        AcceptingNewPatients = false
    };

    private static void AssertAdjudicatable(SyntheticProvider provider)
    {
        Assert.True(provider.IsParticipating);
        Assert.Equal("InNetwork", provider.NetworkStatus);
        Assert.Equal("Active", provider.CredentialingStatus);
        Assert.True(provider.AcceptingNewPatients);
        Assert.Null(provider.TermDate);
        Assert.Equal(new DateTime(2024, 6, 20, 0, 0, 0, DateTimeKind.Utc), provider.EffectiveDate);
    }
}
