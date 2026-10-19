namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkRosterEntry
{
    public string ProviderId { get; set; } = string.Empty;
    public ProviderNetworkRosterProvider Provider { get; set; } = new();
    public ProviderNetworkRosterParticipation Participation { get; set; } = new();
    public ProviderNetworkRosterIntegrity? IntegrityScore { get; set; }
}
