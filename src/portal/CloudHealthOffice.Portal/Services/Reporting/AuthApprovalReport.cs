namespace CloudHealthOffice.Portal.Services;

public class AuthApprovalReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int TotalRequests { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int PendingCount { get; set; }
    public double ApprovalRate { get; set; }
    public double AvgDecisionDays { get; set; }
    public List<AuthByServiceType> ByServiceType { get; set; } = new();
}
