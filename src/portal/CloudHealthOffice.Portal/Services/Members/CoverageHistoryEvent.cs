namespace CloudHealthOffice.Portal.Services;

public class CoverageHistoryEvent
{
    public string EventId { get; set; } = string.Empty;
    public DateTime EventDate { get; set; }
    public string EventType { get; set; } = string.Empty; // Enrolled, PlanChange, PcpChange, Terminated, Reinstated
    public string Description { get; set; } = string.Empty;
    public string? ChangedBy { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
