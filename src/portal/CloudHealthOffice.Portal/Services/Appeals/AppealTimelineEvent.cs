namespace CloudHealthOffice.Portal.Services;

public class AppealTimelineEvent
{
    public DateTime EventDate { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;
}
