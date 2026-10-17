namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkInfo
{
    public string OrganizationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NetworkType { get; set; } = string.Empty;
    public string LineOfBusiness { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string VersionState { get; set; } = string.Empty;
}
