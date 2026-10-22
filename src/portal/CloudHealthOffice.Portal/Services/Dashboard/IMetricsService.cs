namespace CloudHealthOffice.Portal.Services;

public interface IMetricsService
{
    Task<DashboardMetrics> GetDashboardMetricsAsync();
}
