namespace CloudHealthOffice.Portal.Services;

public class AppealsSummary
{
    public int OpenAppeals { get; set; }
    public int UrgentExpedited { get; set; }
    public int DueThisWeek { get; set; }
    public double OverturnedRate { get; set; }
}
