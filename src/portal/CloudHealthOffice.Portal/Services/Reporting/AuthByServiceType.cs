namespace CloudHealthOffice.Portal.Services;

public class AuthByServiceType
{
    public string ServiceType { get; set; } = string.Empty;
    public int Count { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public double ApprovalRate { get; set; }
    public double AvgDecisionDays { get; set; }
}
