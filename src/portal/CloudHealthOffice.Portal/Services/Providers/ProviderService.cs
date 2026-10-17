using System.Net.Http.Json;
using System.Net;

namespace CloudHealthOffice.Portal.Services;

public class ProviderService : IProviderService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderService> _logger;

    public ProviderService(HttpClient httpClient, IConfiguration configuration, ILogger<ProviderService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var providers = await _httpClient.GetFromJsonAsync<List<ProviderSummary>>($"{baseUrl}/providers/search?q={searchTerm}");
            return providers ?? new List<ProviderSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<List<ProviderListItem>> SearchProvidersAsync(string? specialty = null, string? networkStatus = null, string? searchTerm = null)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var query = $"{baseUrl}/providers/list?";
            if (!string.IsNullOrEmpty(specialty))
                query += $"specialty={specialty}&";
            if (!string.IsNullOrEmpty(networkStatus))
                query += $"networkStatus={networkStatus}&";
            if (!string.IsNullOrEmpty(searchTerm))
                query += $"search={searchTerm}";

            var providers = await _httpClient.GetFromJsonAsync<List<ProviderListItem>>(query);
            return providers ?? new List<ProviderListItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderDetails?> GetProviderByIdAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<ProviderDetails>($"{baseUrl}/providers/{providerId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<string> CreateProviderAsync(CreateProviderRequest request)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/providers", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<dynamic>();
            return result?.providerId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task UpdateProviderAsync(string providerId, UpdateProviderRequest request)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/providers/{providerId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderNetworkInfo?> GetNetworkAsync(string networkId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{baseUrl}/v1/networks/{Uri.EscapeDataString(networkId)}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderNetworkInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderNetworkRoster?> GetNetworkRosterAsync(
        string networkId, DateTime asOfDate, int pageSize = 25)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var url = $"{baseUrl}/v1/networks/{Uri.EscapeDataString(networkId)}/roster" +
                      $"?asOfDate={asOfDate:yyyy-MM-dd}&pageSize={Math.Clamp(pageSize, 1, 200)}";
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderNetworkRoster>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderNetworkMembership?> GetNetworkMembershipAsync(
        string networkId, string npi, DateTime asOfDate)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var url = $"{baseUrl}/v1/networks/{Uri.EscapeDataString(networkId)}/members/" +
                      $"{Uri.EscapeDataString(npi)}?asOf={asOfDate:yyyy-MM-dd}";
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderNetworkMembership>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderIntegrityRefreshResult?> RefreshProviderVerificationAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            // Capability 5.10 — on-demand refresh routes through the
            // existing 5.4.5 endpoint (POST /providers/{id}/verification/refresh
            // on ProvidersController, NOT IntegrityProjectionAdminController).
            var response = await _httpClient.PostAsync(
                $"{baseUrl}/providers/{providerId}/verification/refresh",
                content: null);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderIntegrityRefreshResult>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<List<string>> GetSpecialtiesAsync()
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var specialties = await _httpClient.GetFromJsonAsync<List<string>>($"{baseUrl}/providers/specialties");
            return specialties ?? new List<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }
}
