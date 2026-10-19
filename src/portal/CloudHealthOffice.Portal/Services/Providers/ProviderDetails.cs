namespace CloudHealthOffice.Portal.Services;

public class ProviderDetails : ProviderListItem
{
    public string TaxonomyCode { get; set; } = string.Empty;
    public List<string> BoardCertifications { get; set; } = new();
    public List<PracticeLocation> Locations { get; set; } = new();
    public List<ProviderCredential> Credentials { get; set; } = new();
    public List<NetworkAssignment> NetworkAssignments { get; set; } = new();
    public ProviderContract? Contract { get; set; }
    public ProviderPerformance? Performance { get; set; }

    // Detail-only integrity projection metadata (capability 5.10).
    // The provider list grid uses IntegrityScore + IntegrityRating
    // (inherited from ProviderListItem); the detail card additionally
    // surfaces verification timing for operator visibility.
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }
}
