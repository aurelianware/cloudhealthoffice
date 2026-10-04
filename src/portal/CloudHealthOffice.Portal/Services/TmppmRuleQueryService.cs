using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Portal.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// TMPPM PA rules for the PA Rule Explorer, read from terminology-service's
/// API (<c>/api/v1/tmppm/*</c>, terminology:read) with the user's CHO token.
/// The portal no longer opens terminology-service's database
/// (<c>cho_terminology</c>): that service owns it and authorizes every read.
/// TMPPM rules are published state policy, the same for every tenant, so the
/// former tenant filter is gone (the rules never carried a tenant).
/// </summary>
public class TmppmRuleQueryService : ITmppmRuleQueryService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TmppmRuleQueryService> _logger;

    public TmppmRuleQueryService(HttpClient http, IConfiguration configuration, ILogger<TmppmRuleQueryService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => (_configuration["Services:TerminologyService"] ?? "http://terminology-service.cloudhealthoffice").TrimEnd('/');

    private string Url(string path, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(q => !string.IsNullOrEmpty(q.Value))
            .Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}");
        var qs = string.Join("&", parts);
        return $"{BaseUrl}/api/v1/tmppm/{path}" + (qs.Length > 0 ? "?" + qs : string.Empty);
    }

    private async Task<T?> GetAsync<T>(string url, bool notFoundIsNull = false)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
                return default;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Terminology Service (TMPPM)");
            throw new ServiceUnavailableException("Terminology Service", ex);
        }
    }

    public async Task<List<TmppmPaRuleViewModel>> SearchByCodeAsync(string code, string? tenantId = null, string? state = null)
        => await GetAsync<List<TmppmPaRuleViewModel>>(Url("rules", ("code", code.Trim().ToUpperInvariant()), ("state", state))) ?? [];

    public async Task<List<PaCategoryGroup>> GetCategoriesAsync(string state = "TX")
        => await GetAsync<List<PaCategoryGroup>>(Url("categories", ("state", state))) ?? [];

    public async Task<List<TmppmPaRuleViewModel>> GetRulesByCategoryAsync(string category, string? tenantId = null, string? state = null)
        => await GetAsync<List<TmppmPaRuleViewModel>>(Url("rules", ("category", category), ("state", state))) ?? [];

    public Task<TmppmEditionViewModel?> GetCurrentEditionAsync()
        => GetAsync<TmppmEditionViewModel>(Url("editions/current"), notFoundIsNull: true);

    public async Task<List<TmppmEditionViewModel>> GetAllEditionsAsync()
        => await GetAsync<List<TmppmEditionViewModel>>(Url("editions", ("limit", "12"))) ?? [];

    public Task<TmppmDiffViewModel?> GetDiffAsync(string fromEdition, string toEdition)
        => GetAsync<TmppmDiffViewModel>(Url("diffs", ("from", fromEdition), ("to", toEdition)), notFoundIsNull: true);

    public async Task<List<string>> AutocompleteCodeAsync(string prefix, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return [];
        return await GetAsync<List<string>>(Url("codes", ("prefix", prefix.Trim()), ("max", maxResults.ToString()))) ?? [];
    }
}
