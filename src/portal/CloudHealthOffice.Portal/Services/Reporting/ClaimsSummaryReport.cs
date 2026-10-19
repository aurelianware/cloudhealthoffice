namespace CloudHealthOffice.Portal.Services;

public class ClaimsSummaryReport
{
    public DateTime PeriodFrom { get; set; }
    public DateTime PeriodTo { get; set; }
    public int TotalClaims { get; set; }
    public decimal TotalCharges { get; set; }
    public decimal TotalAllowed { get; set; }
    public decimal TotalPaid { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int PendedCount { get; set; }
    public double ApprovalRate { get; set; }
    public decimal AvgClaimAmount { get; set; }
    public List<ClaimsByDateBucket> DailyBreakdown { get; set; } = new();
    public List<ClaimsByProvider> TopProviders { get; set; } = new();
    public List<ClaimsByDiagnosis> TopDiagnoses { get; set; } = new();
}
