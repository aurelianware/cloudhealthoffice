using System.Net.Http.Json;
using System.Net;

namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Appeals Service
// ---------------------------------------------------------------------------

public class AppealsService : IAppealsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppealsService> _logger;

    public AppealsService(HttpClient httpClient, IConfiguration configuration, ILogger<AppealsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AppealsSummary> GetSummaryAsync()
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<AppealsSummary>($"{baseUrl}/appeals/summary");
            return summary ?? new AppealsSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    public async Task<List<AppealSummary>> SearchAppealsAsync(string? appealId = null,
        string? memberId = null, string? originalClaimId = null)
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            // appeals-service has no free-text search: an appeal number and an
            // original claim id each have their own route; member search goes
            // through /appeals/search. List routes return { "items": [...] }.
            List<AppealWire> wires;
            if (!string.IsNullOrWhiteSpace(appealId))
            {
                var one = await GetWireOrNullAsync($"{baseUrl}/appeals/number/{Uri.EscapeDataString(appealId)}");
                wires = one is null ? new() : new() { one };
            }
            else if (!string.IsNullOrWhiteSpace(originalClaimId))
            {
                var list = await _httpClient.GetFromJsonAsync<AppealListWire>(
                    $"{baseUrl}/appeals/claim/{Uri.EscapeDataString(originalClaimId)}");
                wires = list?.Items ?? new();
            }
            else
            {
                var query = string.IsNullOrWhiteSpace(memberId) ? string.Empty : $"memberId={Uri.EscapeDataString(memberId)}";
                var list = await _httpClient.GetFromJsonAsync<AppealListWire>($"{baseUrl}/appeals/search?{query}");
                wires = list?.Items ?? new();
            }

            var now = DateTime.UtcNow;
            return wires.Select(w => AppealWireMapper.ToSummary(w, now)).ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    public async Task<AppealDetails?> GetAppealByIdAsync(string appealId)
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            var wire = await GetWireOrNullAsync($"{baseUrl}/appeals/{Uri.EscapeDataString(appealId)}");
            return wire is null ? null : AppealWireMapper.ToDetails(wire, DateTime.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    private async Task<AppealWire?> GetWireOrNullAsync(string url)
    {
        using var response = await _httpClient.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AppealWire>();
    }
}
