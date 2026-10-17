namespace CloudHealthOffice.Portal.Services;

public class ProviderNetworkRoster
{
    public List<ProviderNetworkRosterEntry> Items { get; set; } = new();
    public string? NextCursor { get; set; }
    public int PageSize { get; set; }
}
