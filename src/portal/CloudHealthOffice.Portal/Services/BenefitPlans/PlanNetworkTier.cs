namespace CloudHealthOffice.Portal.Services;

public class PlanNetworkTier
{
    public string Id { get; set; } = string.Empty;
    public string TierName { get; set; } = string.Empty;
    public int TierLevel { get; set; } = 1;
    public string NetworkId { get; set; } = string.Empty;
}
