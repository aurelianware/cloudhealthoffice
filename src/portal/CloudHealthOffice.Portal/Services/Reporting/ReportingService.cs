using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── Reporting Service ───────────────────────────────────────────────────

public class ReportingService : IReportingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportingService> _logger;

    public ReportingService(HttpClient httpClient, IConfiguration configuration, ILogger<ReportingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ClaimsSummaryReport> GetClaimsSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/claims-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<ClaimsSummaryReport>()
                ?? throw new Exception("Empty response from claims summary report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<PaymentSummaryReport> GetPaymentSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/payment-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<PaymentSummaryReport>()
                ?? throw new Exception("Empty response from payment summary report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<EligibilityStatsReport> GetEligibilityStatsAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:EligibilityService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/eligibility-stats", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<EligibilityStatsReport>()
                ?? throw new Exception("Empty response from eligibility stats report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Eligibility Service");
            throw new ServiceUnavailableException("Eligibility Service", ex);
        }
    }

    public async Task<AuthApprovalReport> GetAuthApprovalReportAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/auth-approval", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<AuthApprovalReport>()
                ?? throw new Exception("Empty response from auth approval report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<List<ClaimsByProvider>> GetProviderPerformanceAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/provider-performance", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<List<ClaimsByProvider>>() ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}
