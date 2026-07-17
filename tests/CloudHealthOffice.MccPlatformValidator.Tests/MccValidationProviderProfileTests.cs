using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccValidationProviderProfileTests
{
    [Fact]
    public void ForceAdjudicatable_AnchorsProviderBeforeServiceDate()
    {
        var provider = new SyntheticProvider
        {
            NetworkStatus = "Excluded",
            CredentialingStatus = "Excluded",
            IsParticipating = false,
            AcceptingNewPatients = false,
            EffectiveDate = new DateTime(2026, 7, 1),
            TermDate = new DateTime(2026, 7, 10)
        };

        MccValidationProviderProfile.ForceAdjudicatable(provider, new DateTime(2026, 6, 20));

        Assert.True(provider.IsParticipating);
        Assert.Equal("InNetwork", provider.NetworkStatus);
        Assert.Equal("Active", provider.CredentialingStatus);
        Assert.True(provider.AcceptingNewPatients);
        Assert.Null(provider.TermDate);
        Assert.Equal(new DateTime(2024, 6, 20, 0, 0, 0, DateTimeKind.Utc), provider.EffectiveDate);
    }

    [Fact]
    public void ForceCleanProfessionalPaid_KeepsAdjudicatableProfileAndProfessionalMetadata()
    {
        var provider = new SyntheticProvider
        {
            State = "TX",
            SpecialtyCode = "1223G0001X",
            SpecialtyDescription = "Dentist",
            TaxonomyCode = "1223G0001X",
            ContractType = "Capitated",
            EffectiveDate = new DateTime(2026, 7, 1)
        };

        MccValidationProviderProfile.ForceCleanProfessionalPaid(provider, new DateTime(2026, 6, 20));

        Assert.Equal("AZ", provider.State);
        Assert.Equal("207Q00000X", provider.SpecialtyCode);
        Assert.Equal("Family Medicine", provider.SpecialtyDescription);
        Assert.Equal("207Q00000X", provider.TaxonomyCode);
        Assert.Equal("FeeForService", provider.ContractType);
        Assert.Equal("InNetwork", provider.NetworkStatus);
        Assert.Equal("Active", provider.CredentialingStatus);
        Assert.Equal(new DateTime(2024, 6, 20, 0, 0, 0, DateTimeKind.Utc), provider.EffectiveDate);
    }
}
