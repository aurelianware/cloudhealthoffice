namespace CloudHealthOffice.Portal.Services;

public class CorrespondenceSummary
{
    public int PendingGeneration { get; set; }
    public int GeneratedToday { get; set; }
    public int SentThisWeek { get; set; }
    public int FailedReturned { get; set; }
}
