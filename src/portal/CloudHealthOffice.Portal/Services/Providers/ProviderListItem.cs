namespace CloudHealthOffice.Portal.Services;

public class ProviderListItem
{
    public string ProviderId { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PracticeType { get; set; } = string.Empty; // Individual, Group
    public string Specialty { get; set; } = string.Empty;
    public string PracticeName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty; // In-Network, Out-of-Network, Pending
    public string CredentialingStatus { get; set; } = string.Empty; // Active, Pending, Expired
    public int NetworkCount { get; set; }
    public DateTime? LastClaimDate { get; set; }

    // Cached integrity projection (capability 5.10). Populated by
    // ProviderIntegrityProjectionService in provider-service; null
    // until the projection worker has produced a score for the
    // provider. Rendered by IntegrityBadge on the provider grid.
    public int? IntegrityScore { get; set; }
    public string? IntegrityRating { get; set; }
}
