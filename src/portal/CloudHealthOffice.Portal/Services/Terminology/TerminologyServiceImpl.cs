using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

// ── Terminology Service ─────────────────────────────────────────────────────

public class TerminologyServiceImpl : ITerminologyService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TerminologyServiceImpl> _logger;

    public TerminologyServiceImpl(HttpClient httpClient, IConfiguration configuration, ILogger<TerminologyServiceImpl> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:TerminologyService"] ?? "http://terminology-service.cloudhealthoffice";

    public async Task<TermTranslateResult> TranslateAsync(string system, string code, string targetSystem,
        string? tenantId = null, int? age = null, string? gender = null, string? state = null)
    {
        try
        {
            var qs = new List<string>
            {
                $"system={Uri.EscapeDataString(system)}",
                $"code={Uri.EscapeDataString(code)}",
                $"target={Uri.EscapeDataString(targetSystem)}"
            };
            if (!string.IsNullOrEmpty(tenantId)) qs.Add($"tenantId={Uri.EscapeDataString(tenantId)}");
            if (age.HasValue) qs.Add($"age={age.Value}");
            if (!string.IsNullOrEmpty(gender)) qs.Add($"gender={Uri.EscapeDataString(gender)}");
            if (!string.IsNullOrEmpty(state)) qs.Add($"state={Uri.EscapeDataString(state)}");

            var query = string.Join("&", qs);
            return await _httpClient.GetFromJsonAsync<TermTranslateResult>(
                $"{BaseUrl}/fhir/ConceptMap/$translate?{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Terminology Service unavailable"); throw new ServiceUnavailableException("Terminology Service", ex); }
    }

    public async Task<List<TermMapVersionSummary>> GetMapVersionsAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<List<TermMapVersionSummary>>($"{BaseUrl}/admin/maps") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Terminology Service unavailable"); throw new ServiceUnavailableException("Terminology Service", ex); }
    }

    public async Task<TermHealthStatus> GetHealthAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<TermHealthStatus>($"{BaseUrl}/health") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Terminology Service unavailable"); throw new ServiceUnavailableException("Terminology Service", ex); }
    }
}
