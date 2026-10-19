using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public class FamilyRelationshipService : IFamilyRelationshipService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FamilyRelationshipService> _logger;

    public FamilyRelationshipService(HttpClient httpClient, IConfiguration configuration, ILogger<FamilyRelationshipService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:MemberService"] ?? string.Empty;

    public async Task<List<FamilyRelationshipRow>> ListForMemberAsync(string memberId)
    {
        try
        {
            var payload = await _httpClient.GetFromJsonAsync<FamilyRelationshipListResponse>(
                $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships");
            return payload?.Relationships ?? new List<FamilyRelationshipRow>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<FamilyRelationshipRow?> AddDependentAsync(string subscriberMemberId, AddDependentPayload payload)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/members/{Uri.EscapeDataString(subscriberMemberId)}/dependents",
                payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Add dependent failed ({(int)response.StatusCode}): {body}");
            }

            // Server returns { member, subscriberMemberId, relationship }. Surface the
            // relationship row so callers can link into the new edge without a re-fetch.
            var parsed = await response.Content.ReadFromJsonAsync<AddDependentApiResponse>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.Relationship;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    private class AddDependentApiResponse
    {
        public FamilyRelationshipRow? Relationship { get; set; }
        public string SubscriberMemberId { get; set; } = string.Empty;
    }

    public async Task EndRelationshipAsync(string memberId, string relationshipId, DateTime? endDate = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships/{Uri.EscapeDataString(relationshipId)}/end",
                new { endDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<FamilyRelationshipRow?> UpdateRelationshipAsync(string memberId, string relationshipId, UpdateRelationshipPayload payload)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync(
                $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships/{Uri.EscapeDataString(relationshipId)}",
                payload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<FamilyRelationshipRow>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task SoftDeleteAsync(string memberId, string relationshipId, string reason)
    {
        try
        {
            var url = $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships/{Uri.EscapeDataString(relationshipId)}?reason={Uri.EscapeDataString(reason)}";
            var response = await _httpClient.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    private class FamilyRelationshipListResponse
    {
        public string MemberId { get; set; } = string.Empty;
        public List<FamilyRelationshipRow> Relationships { get; set; } = new();
        public int TotalCount { get; set; }
    }
}
