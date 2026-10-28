using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Calls correspondence-service (<c>Services:CorrespondenceService</c>). The
/// service checks correspondence:read and, for letters, read access to one of
/// each letter's subjects.
/// </summary>
public class CorrespondenceService : ICorrespondenceService
{
    public const string ServiceName = "Correspondence Service";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CorrespondenceService> _logger;

    public CorrespondenceService(HttpClient httpClient, IConfiguration configuration, ILogger<CorrespondenceService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CorrespondenceSummary> GetSummaryAsync() =>
        await GetAsync<CorrespondenceSummary>("letters/summary") ?? new CorrespondenceSummary();

    public async Task<List<CorrespondenceLetter>> GetRecentLettersAsync(string? letterTypeCode = null, string? status = null, int limit = 100)
    {
        var path = $"letters/recent?limit={limit}";
        if (!string.IsNullOrWhiteSpace(letterTypeCode)) path += $"&letterTypeCode={Uri.EscapeDataString(letterTypeCode)}";
        if (!string.IsNullOrWhiteSpace(status)) path += $"&status={Uri.EscapeDataString(status)}";
        return await GetAsync<List<CorrespondenceLetter>>(path) ?? new();
    }

    public async Task<List<CorrespondenceLetterType>> GetLetterTypesAsync() =>
        await GetAsync<List<CorrespondenceLetterType>>("letter-types") ?? new();

    public async Task<List<CorrespondenceTemplate>> GetTemplatesAsync() =>
        await GetAsync<List<CorrespondenceTemplate>>("templates") ?? new();

    public async Task<LetterRequestResult> RequestLetterAsync(CorrespondenceLetterRequest request)
    {
        var baseUrl = (_configuration["Services:CorrespondenceService"] ?? "http://correspondence-service.cloudhealthoffice").TrimEnd('/');
        try
        {
            using var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/api/v1/correspondence/letters", request);
            if (response.IsSuccessStatusCode)
                return new(await response.Content.ReadFromJsonAsync<CorrespondenceLetter>(), null);
            string? error = null;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && json.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
            }
            catch (System.Text.Json.JsonException) { }
            return new(null, error ?? $"The letter was not produced ({(int)response.StatusCode}).");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    private async Task<T?> GetAsync<T>(string path)
    {
        var baseUrl = (_configuration["Services:CorrespondenceService"] ?? "http://correspondence-service.cloudhealthoffice").TrimEnd('/');
        try
        {
            return await _httpClient.GetFromJsonAsync<T>($"{baseUrl}/api/v1/correspondence/{path}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }
}
