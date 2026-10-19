namespace CloudHealthOffice.Portal.Services;

public interface IMetricsService
{
    Task<DashboardMetrics> GetDashboardMetricsAsync();
    Task<OperationalAlerts> GetOperationalAlertsAsync();
    Task<EdiVolumeSummary> GetTodayEdiVolumeAsync();
}
