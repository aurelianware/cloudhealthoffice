using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccProviderFixturePoolTests
{
    [Fact]
    public void Apply_ReusesEquivalentProvidersByRole()
    {
        var claims = new[]
        {
            Claim("MCC-P-1", Provider("1000000001"), Provider("2000000001")),
            Claim("MCC-P-2", Provider("1000000002"), Provider("2000000002"))
        };

        var result = MccProviderFixturePool.Apply(claims, 42, RunId);

        Assert.Equal(4, result.ProvidersBefore);
        Assert.Equal(2, result.ProvidersAfter);
        Assert.Equal(2, result.ReusedAssignments);
        Assert.Same(claims[0].BillingProvider, claims[1].BillingProvider);
        Assert.Same(claims[0].RenderingProvider, claims[1].RenderingProvider);
    }

    [Fact]
    public void Apply_DoesNotReuseDifferentAdjudicationProfiles()
    {
        var active = Provider("1000000001");
        var excluded = Provider("1000000002");
        excluded.CredentialingStatus = "Excluded";
        excluded.NetworkStatus = "Excluded";

        var claims = new[]
        {
            Claim("MCC-P-1", active, Provider("2000000001")),
            Claim("MCC-P-2", excluded, Provider("2000000002"))
        };

        var result = MccProviderFixturePool.Apply(claims, 42, RunId);

        Assert.Equal(3, result.ProvidersAfter);
        Assert.NotSame(claims[0].BillingProvider, claims[1].BillingProvider);
    }

    [Fact]
    public void Apply_PreservesProviderSensitivePriorAuthorizationClaims()
    {
        var first = Claim("MCC-E-1", Provider("1000000001"), Provider("2000000001"));
        first.EdgeCase = EdgeCaseScenario.PriorAuthRequired_WrongProvider;
        first.PriorAuthStatus = "OnFile";
        var second = Claim("MCC-I-2", Provider("1000000002"), Provider("2000000002"));
        second.PriorAuthStatus = "Required";

        var result = MccProviderFixturePool.Apply(new[] { first, second }, 42, RunId);

        Assert.Equal(4, result.ProvidersAfter);
        Assert.Equal(2, result.ProtectedClaims);
        Assert.Equal("1000000001", first.BillingProvider.Npi);
        Assert.Equal("1000000002", second.BillingProvider.Npi);
    }

    private static SyntheticClaim Claim(
        string id,
        SyntheticProvider billingProvider,
        SyntheticProvider renderingProvider) => new()
    {
        ClaimId = id,
        BillingProvider = billingProvider,
        RenderingProvider = renderingProvider,
        PriorAuthStatus = "NotRequired"
    };

    private static SyntheticProvider Provider(string npi) => new()
    {
        Npi = npi,
        ProviderType = "Individual",
        SpecialtyCode = "207Q00000X",
        TaxonomyCode = "207Q00000X",
        IsParticipating = true,
        NetworkStatus = "InNetwork",
        CredentialingStatus = "Active",
        State = "AZ",
        ContractType = "FeeForService",
        FeeScheduleId = "FS-MEDICAID",
        EffectiveDate = new DateTime(2024, 1, 1),
        AcceptingNewPatients = true
    };

    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");
}
