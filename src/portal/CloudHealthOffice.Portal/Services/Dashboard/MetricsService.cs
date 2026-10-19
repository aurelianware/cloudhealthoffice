using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// TEMPORARY: Replace when Prometheus is deployed.
// MetricsService retains mock data until real Prometheus metrics are wired up.
public class MetricsService : IMetricsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsService> _logger;

    public MetricsService(HttpClient httpClient, IConfiguration configuration, ILogger<MetricsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<ClaimsSummaryResponse>($"{baseUrl}/claims/summary");
            if (summary == null) return new DashboardMetrics();

            var total = summary.TotalClaims;
            return new DashboardMetrics
            {
                TotalClaims = total,
                ClaimsTrend = 0,
                ApprovalRate = total > 0 ? (double)summary.ApprovedClaims / total : 0,
                AvgProcessingTimeMs = (int)summary.AverageProcessingDays * 24 * 60,
                TotalPayerAmount = summary.TotalPaidAmount,
                ApprovedClaims = summary.ApprovedClaims,
                DeniedClaims = summary.DeniedClaims,
                PendingClaims = summary.PendedClaims
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    private class ClaimsSummaryResponse
    {
        public int TotalClaims { get; set; }
        public int ApprovedClaims { get; set; }
        public int DeniedClaims { get; set; }
        public int PendedClaims { get; set; }
        public int PaidClaims { get; set; }
        public decimal TotalChargeAmount { get; set; }
        public decimal TotalAllowedAmount { get; set; }
        public decimal TotalPaidAmount { get; set; }
        public decimal AverageProcessingDays { get; set; }
        public decimal ApprovalRate { get; set; }
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<OperationalAlerts> GetOperationalAlertsAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var alerts = await _httpClient.GetFromJsonAsync<OperationalAlerts>($"{baseUrl}/metrics/operational-alerts");
            return alerts ?? new OperationalAlerts();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<EdiVolumeSummary> GetTodayEdiVolumeAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var volume = await _httpClient.GetFromJsonAsync<EdiVolumeSummary>($"{baseUrl}/metrics/edi-volume/today");
            return volume ?? new EdiVolumeSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

}
