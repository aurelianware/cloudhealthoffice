namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkRosterIntegrity
{
    public int? Score { get; set; }
    public string? Rating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
}
