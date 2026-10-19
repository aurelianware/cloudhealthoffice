namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkRosterProvider
{
    public string Npi { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PrimarySpecialty { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? State { get; set; }
    public bool AcceptingNewPatients { get; set; }
}
