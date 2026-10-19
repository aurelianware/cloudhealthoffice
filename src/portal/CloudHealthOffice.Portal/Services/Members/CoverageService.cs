using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class CoverageService : ICoverageService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CoverageService> _logger;

    public CoverageService(HttpClient httpClient, IConfiguration configuration, ILogger<CoverageService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Coverage>> GetCoverageByMemberIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:CoverageService"];
        try
        {
            var coverage = await _httpClient.GetFromJsonAsync<List<Coverage>>($"{baseUrl}/v1/coverage/member/{memberId}/history");
            return coverage ?? new List<Coverage>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Coverage Service");
            throw new ServiceUnavailableException("Coverage Service", ex);
        }
    }
}
