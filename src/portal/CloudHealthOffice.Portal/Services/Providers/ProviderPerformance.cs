namespace CloudHealthOffice.Portal.Services;

public class ProviderPerformance
{
    public int ClaimsLast90Days { get; set; }
    public decimal TotalBilledLast90Days { get; set; }
    public decimal AvgClaimAmount { get; set; }
    public int AuthorizationRequests { get; set; }
    public decimal AuthorizationApprovalRate { get; set; }
    public int DenialCount { get; set; }
    public decimal DenialRate { get; set; }
    public decimal AvgProcessingTimeDays { get; set; }
    public decimal? QualityScore { get; set; }
}
