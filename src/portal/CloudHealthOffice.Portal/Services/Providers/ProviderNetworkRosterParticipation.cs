namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkRosterParticipation
{
    public string? PlanId { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public string NetworkTier { get; set; } = string.Empty;
    public bool AcceptingNewPatients { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}
