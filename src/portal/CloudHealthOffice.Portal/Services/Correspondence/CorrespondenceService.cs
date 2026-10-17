using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Correspondence Service
// ---------------------------------------------------------------------------

public class CorrespondenceService : ICorrespondenceService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CorrespondenceService> _logger;

    public CorrespondenceService(HttpClient httpClient, IConfiguration configuration, ILogger<CorrespondenceService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CorrespondenceSummary> GetSummaryAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<CorrespondenceSummary>($"{baseUrl}/correspondence/summary");
            return summary ?? new CorrespondenceSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<CorrespondenceItem>> GetQueueAsync(string? type = null,
        string? status = null, int limit = 50)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/correspondence/queue?limit={limit}";
            if (!string.IsNullOrEmpty(type)) url += $"&type={Uri.EscapeDataString(type)}";
            if (!string.IsNullOrEmpty(status)) url += $"&status={Uri.EscapeDataString(status)}";
            var items = await _httpClient.GetFromJsonAsync<List<CorrespondenceItem>>(url);
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<RfaiTrackingItem>> GetOutstandingRfaisAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var items = await _httpClient.GetFromJsonAsync<List<RfaiTrackingItem>>($"{baseUrl}/correspondence/rfais/outstanding");
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}
