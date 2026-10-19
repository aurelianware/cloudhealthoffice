namespace CloudHealthOffice.Portal.Services;

public class DashboardMetrics
{
    public int TotalClaims { get; set; }
    public double ClaimsTrend { get; set; }
    public double ApprovalRate { get; set; }
    public int AvgProcessingTimeMs { get; set; }
    public decimal TotalPayerAmount { get; set; }
    public int ApprovedClaims { get; set; }
    public int DeniedClaims { get; set; }
    public int PendingClaims { get; set; }
}
