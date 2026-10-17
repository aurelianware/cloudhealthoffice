using System.Net.Http.Json;
using System.Net;

namespace CloudHealthOffice.Portal.Services;

public class AuthorizationService : IAuthorizationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthorizationService> _logger;

    // The CHO token for these calls is attached by ChoBearerTokenHandler.
    public AuthorizationService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AuthorizationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthorizationInsightsView> GetAuthorizationInsightsAsync(DateTime from, DateTime to, string? lineOfBusiness = null)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        var query = $"from={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}"
                    + (string.IsNullOrWhiteSpace(lineOfBusiness) ? "" : $"&lineOfBusiness={Uri.EscapeDataString(lineOfBusiness)}");
        try
        {
            return await _httpClient.GetFromJsonAsync<AuthorizationInsightsView>($"{baseUrl}/authorizations/insights?{query}")
                   ?? new AuthorizationInsightsView();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<List<AuthorizationSummary>> ListAuthorizationInsightAsync(IReadOnlyDictionary<string, string> query)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        // The service names line of business "lineOfBusiness"; the portal URL keeps it short.
        var parts = query
            .Where(kv => AuthorizationDrillDown.Keys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{(kv.Key == "lob" ? "lineOfBusiness" : kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        try
        {
            return await _httpClient.GetFromJsonAsync<List<AuthorizationSummary>>($"{baseUrl}/authorizations/insights/list?{string.Join("&", parts)}")
                   ?? new List<AuthorizationSummary>();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            // The "approved but unused" list needs claim usage from claims-service.
            throw new ServiceUnavailableException("Claims Service", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var url = string.IsNullOrEmpty(memberId)
                ? $"{baseUrl}/authorizations/search"
                : $"{baseUrl}/authorizations/search?memberId={memberId}";
            var auths = await _httpClient.GetFromJsonAsync<List<AuthorizationSummary>>(url);
            return auths ?? new List<AuthorizationSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            // Callers pass either the record id or the authorization number
            // (the list and a claim's prior-auth field show the number): try
            // the id, then the number.
            var key = Uri.EscapeDataString(authorizationId);
            using var byId = await _httpClient.GetAsync($"{baseUrl}/authorizations/{key}");
            if (byId.IsSuccessStatusCode)
                return await byId.Content.ReadFromJsonAsync<AuthorizationDetails>();
            if (byId.StatusCode != System.Net.HttpStatusCode.NotFound)
                byId.EnsureSuccessStatusCode();

            using var byNumber = await _httpClient.GetAsync($"{baseUrl}/authorizations/number/{key}");
            if (byNumber.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            byNumber.EnsureSuccessStatusCode();
            return await byNumber.Content.ReadFromJsonAsync<AuthorizationDetails>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/authorizations", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubmitAuthorizationResponse>();
            return result?.AuthorizationId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    private class SubmitAuthorizationResponse
    {
        public string AuthorizationId { get; set; } = string.Empty;
    }
}
