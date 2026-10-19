namespace CloudHealthOffice.Portal.Services;

public class TermHealthStatus
{
    public string Status { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public int TotalActiveEntries { get; set; }
    public List<TermMapVersionSummary> ActiveMaps { get; set; } = new();
    public DateTime Timestamp { get; set; }
}
