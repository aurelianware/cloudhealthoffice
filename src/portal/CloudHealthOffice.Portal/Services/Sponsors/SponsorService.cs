using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

public class SponsorService : ISponsorService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SponsorService> _logger;

    public SponsorService(HttpClient httpClient, IConfiguration configuration, ILogger<SponsorService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<SponsorSummary>> SearchSponsorsAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var response = await _httpClient.GetFromJsonAsync<JsonElement>($"{baseUrl}/sponsors?search={searchTerm}");
            if (response.TryGetProperty("sponsors", out var sponsorsArray))
            {
                return sponsorsArray.Deserialize<List<SponsorSummary>>() ?? new();
            }
            // Fallback: if the response is a plain array
            return response.Deserialize<List<SponsorSummary>>() ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task<SponsorDetails?> GetSponsorByIdAsync(string sponsorId)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<SponsorDetails>($"{baseUrl}/sponsors/{sponsorId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task<string> CreateSponsorAsync(CreateSponsorRequest request)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var payload = new
            {
                groupNumber = $"GRP-{Guid.NewGuid().ToString()[..8].ToUpper()}",
                employerName = request.Name,
                taxId = request.TaxId,
                address = request.AddressLine1,
                city = request.City,
                state = request.State,
                zipCode = request.ZipCode,
                contactName = request.ContactName,
                contactPhone = request.ContactPhone,
                contactEmail = request.ContactEmail,
                effectiveDate = request.ContractStartDate == default ? DateTime.UtcNow : request.ContractStartDate,
                status = 1, // Active
                lineOfBusiness = 1 // Commercial
            };
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/sponsors", payload);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task UpdateSponsorAsync(string sponsorId, UpdateSponsorRequest request)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/sponsors/{sponsorId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    public async Task<SponsorMemberView?> GetSponsorMemberViewAsync(string groupNumber)
    {
        var baseUrl = _configuration["Services:SponsorService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/sponsors/{Uri.EscapeDataString(groupNumber)}/member-view");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SponsorMemberView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Sponsor Service");
            throw new ServiceUnavailableException("Sponsor Service", ex);
        }
    }

    private class CreateSponsorResponse
    {
        public string SponsorId { get; set; } = string.Empty;
    }
}
