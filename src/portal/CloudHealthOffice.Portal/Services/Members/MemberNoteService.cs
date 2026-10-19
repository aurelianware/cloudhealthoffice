using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class MemberNoteService : IMemberNoteService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberNoteService> _logger;

    public MemberNoteService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberNoteService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<MemberNotePage> ListAsync(string memberId, MemberNoteFilter filter)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var qs = new List<string> { $"pageSize={Math.Clamp(filter.Limit, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(filter.Category)) qs.Add($"category={Uri.EscapeDataString(filter.Category)}");
        if (!string.IsNullOrWhiteSpace(filter.ContinuationToken))
            qs.Add($"continuationToken={Uri.EscapeDataString(filter.ContinuationToken)}");

        try
        {
            var url = $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/notes?{string.Join("&", qs)}";
            var page = await _httpClient.GetFromJsonAsync<MemberNotePage>(url);
            return page ?? new MemberNotePage();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberNoteView?> CreateAsync(string memberId, CreateMemberNotePayload payload)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/notes", payload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberNoteView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}
