namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkMembership
{
    public string NetworkId { get; set; } = string.Empty;
    public string Npi { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public bool IsActiveMember { get; set; }
    public DateTime AsOfDate { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? ParticipationStatus { get; set; }
    public string? LineOfBusiness { get; set; }
    public string? NetworkTier { get; set; }
}
