using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

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
            var term = searchTerm?.Trim() ?? string.Empty;
            if (IsNpi(term))
            {
                // Name search does not match NPIs; an NPI goes to the exact lookup.
                using var response = await _httpClient.GetAsync($"{baseUrl}/providers/npi/{term}");
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new List<ProviderSummary>();
                }

                response.EnsureSuccessStatusCode();
                var single = await response.Content.ReadFromJsonAsync<JsonElement>();
                return ProviderRecordProjection.ToSummaries(
                    single.ValueKind == JsonValueKind.Object ? WrapInArray(single) : single,
                    DateTime.UtcNow);
            }

            var payload = await _httpClient.GetFromJsonAsync<JsonElement>(
                $"{baseUrl}/providers/search?q={Uri.EscapeDataString(term)}");
            return ProviderRecordProjection.ToSummaries(payload, DateTime.UtcNow);
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
            var asOf = DateTime.UtcNow;
            var term = searchTerm?.Trim();
            List<ProviderListItem> providers;

            if (!string.IsNullOrEmpty(term) && IsNpi(term))
            {
                // The NPI box is an exact match: GET /providers/npi/{npi}.
                using var response = await _httpClient.GetAsync($"{baseUrl}/providers/npi/{term}");
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new List<ProviderListItem>();
                }

                response.EnsureSuccessStatusCode();
                var single = await response.Content.ReadFromJsonAsync<JsonElement>();
                providers = single.ValueKind == JsonValueKind.Object
                    ? new List<ProviderListItem> { ProviderRecordProjection.ToListItem(single, asOf) }
                    : ProviderRecordProjection.ToListItems(single, asOf);
            }
            else
            {
                // /providers/list takes no name filter, so a name search goes
                // to /providers/search (first, last or organization name).
                var query = string.IsNullOrEmpty(term)
                    ? $"{baseUrl}/providers/list?"
                    : $"{baseUrl}/providers/search?name={Uri.EscapeDataString(term)}&";
                if (!string.IsNullOrEmpty(specialty))
                    query += $"specialty={Uri.EscapeDataString(specialty)}&";
                query += "pageSize=200";

                var payload = await _httpClient.GetFromJsonAsync<JsonElement>(query);
                providers = ProviderRecordProjection.ToListItems(payload, asOf);
            }

            if (!string.IsNullOrEmpty(specialty))
            {
                providers = providers
                    .Where(p => p.Specialty.Contains(specialty, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // provider-service has no network-status filter; the status is
            // derived from the provider's network participations.
            if (!string.IsNullOrEmpty(networkStatus))
            {
                providers = providers
                    .Where(p => string.Equals(p.NetworkStatus, networkStatus, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return providers;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    private static JsonElement WrapInArray(JsonElement item)
        => JsonDocument.Parse($"[{item.GetRawText()}]").RootElement.Clone();

    private static bool IsNpi(string term) => term.Length == 10 && term.All(char.IsAsciiDigit);

    public async Task<ProviderDetails?> GetProviderByIdAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var payload = await _httpClient.GetFromJsonAsync<JsonElement>($"{baseUrl}/providers/{providerId}");
            return ProviderRecordProjection.ToDetails(payload, DateTime.UtcNow);
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
