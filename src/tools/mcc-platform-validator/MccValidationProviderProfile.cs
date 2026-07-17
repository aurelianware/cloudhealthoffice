using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

public static class MccValidationProviderProfile
{
    public static void ForceAdjudicatable(SyntheticProvider provider, DateTime serviceDate)
    {
        provider.IsParticipating = true;
        provider.NetworkStatus = "InNetwork";
        provider.CredentialingStatus = "Active";
        provider.TermDate = null;
        provider.AcceptingNewPatients = true;
        provider.EffectiveDate = DateTime.SpecifyKind(serviceDate.Date.AddYears(-2), DateTimeKind.Utc);
    }

    public static void ForceCleanProfessionalPaid(SyntheticProvider provider, DateTime serviceDate)
    {
        ForceAdjudicatable(provider, serviceDate);
        provider.State = "AZ";
        provider.SpecialtyCode = "207Q00000X";
        provider.SpecialtyDescription = "Family Medicine";
        provider.TaxonomyCode = "207Q00000X";
        provider.ContractType = "FeeForService";
    }
}
