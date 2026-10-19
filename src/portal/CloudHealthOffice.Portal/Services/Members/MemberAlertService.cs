using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class MemberAlertService : IMemberAlertService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberAlertService> _logger;

    public MemberAlertService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberAlertService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MemberAlertView>> ListAsync(string memberId, bool activeOnly)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var url = $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/alerts";
        if (activeOnly) url += "?status=active";
        try
        {
            var page = await _httpClient.GetFromJsonAsync<MemberAlertListEnvelope>(url);
            return page?.Items ?? new List<MemberAlertView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAlertView?> CreateAsync(string memberId, CreateMemberAlertPayload payload)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/alerts", payload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberAlertView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAlertView?> EndAsync(string memberId, string alertId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/alerts/{Uri.EscapeDataString(alertId)}/end",
                new { });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberAlertView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    private sealed class MemberAlertListEnvelope
    {
        public List<MemberAlertView> Items { get; set; } = new();
    }
}
