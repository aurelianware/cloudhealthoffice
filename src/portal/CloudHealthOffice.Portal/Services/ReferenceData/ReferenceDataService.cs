using System.Net;
using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

public class ReferenceDataService : IReferenceDataService
{
    public const string ServiceName = "Reference Data Service";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReferenceDataService> _logger;

    public ReferenceDataService(HttpClient httpClient, IConfiguration configuration, ILogger<ReferenceDataService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ReferenceCodePage> SearchCodesAsync(string codeSystem, string? search, ReferenceSearchMode mode,
        bool? active = null, int page = 1, int pageSize = 50)
    {
        var url = $"{CodesUrl()}?codeSystem={Uri.EscapeDataString(codeSystem)}&searchMode={mode}&page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (active is { } a) url += $"&active={(a ? "true" : "false")}";
        try
        {
            return await _httpClient.GetFromJsonAsync<ReferenceCodePage>(url) ?? new ReferenceCodePage();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    public async Task<ReferenceCodeRecord?> GetCodeAsync(string codeSystem, string code)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{CodesUrl()}/{Uri.EscapeDataString(codeSystem)}/{Uri.EscapeDataString(code)}");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ReferenceCodeRecord>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    /// <summary>Services:ReferenceDataService is the service root.</summary>
    private string CodesUrl() =>
        (_configuration["Services:ReferenceDataService"] ?? "http://reference-data-service.cloudhealthoffice").TrimEnd('/')
        + "/api/reference-data/codes";
}
