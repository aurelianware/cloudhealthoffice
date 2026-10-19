using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class ReferenceDataService : IReferenceDataService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReferenceDataService> _logger;

    public ReferenceDataService(HttpClient httpClient, IConfiguration configuration, ILogger<ReferenceDataService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MedicalCode>> SearchCodesAsync(string? codeSystem = null, string? searchTerm = null)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            var query = $"{baseUrl}/codes?";
            if (!string.IsNullOrEmpty(codeSystem))
                query += $"codeSystem={codeSystem}&";
            if (!string.IsNullOrEmpty(searchTerm))
                query += $"search={searchTerm}";

            var codes = await _httpClient.GetFromJsonAsync<List<MedicalCode>>(query);
            return codes ?? new List<MedicalCode>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }

    public async Task<MedicalCodeDetails?> GetCodeDetailsAsync(string codeSystem, string code)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MedicalCodeDetails>($"{baseUrl}/codes/{codeSystem}/{code}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }

    public async Task<List<string>> GetCodeSystemsAsync()
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            var systems = await _httpClient.GetFromJsonAsync<List<string>>($"{baseUrl}/code-systems");
            return systems ?? new List<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }

    public async Task<CodeUsageStats?> GetCodeUsageStatsAsync(string codeSystem, string code)
    {
        var baseUrl = _configuration["Services:ReferenceDataService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<CodeUsageStats>($"{baseUrl}/codes/{codeSystem}/{code}/usage");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Reference Data Service");
            throw new ServiceUnavailableException("Reference Data Service", ex);
        }
    }
}
