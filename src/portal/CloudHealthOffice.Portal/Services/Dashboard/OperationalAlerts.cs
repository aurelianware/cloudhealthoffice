namespace CloudHealthOffice.Portal.Services;

public class OperationalAlerts
{
    public int WorkQueueCount { get; set; }
    public int PendingRfais { get; set; }
    public int AppealsDueThisWeek { get; set; }
    public int ApproachingFilingLimit { get; set; }
}
