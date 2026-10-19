namespace CloudHealthOffice.Portal.Services;

// Reporting
public interface IReportingService
{
    Task<ClaimsSummaryReport> GetClaimsSummaryAsync(ReportRequest request);
    Task<PaymentSummaryReport> GetPaymentSummaryAsync(ReportRequest request);
    Task<EligibilityStatsReport> GetEligibilityStatsAsync(ReportRequest request);
    Task<AuthApprovalReport> GetAuthApprovalReportAsync(ReportRequest request);
    Task<List<ClaimsByProvider>> GetProviderPerformanceAsync(ReportRequest request);
}
