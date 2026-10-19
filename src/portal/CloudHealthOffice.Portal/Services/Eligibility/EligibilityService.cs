using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class EligibilityService : IEligibilityService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EligibilityService> _logger;

    public EligibilityService(HttpClient httpClient, IConfiguration configuration, ILogger<EligibilityService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EligibilityResponse> CheckEligibilityAsync(object request)
    {
        var baseUrl = _configuration["Services:EligibilityService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/eligibility/inquiry", request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<EligibilityResponse>()
                ?? throw new Exception("No response from eligibility service");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Eligibility Service");
            throw new ServiceUnavailableException("Eligibility Service", ex);
        }
    }
}
