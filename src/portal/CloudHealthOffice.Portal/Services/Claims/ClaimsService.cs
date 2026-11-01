using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public class ClaimsService : IClaimsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimsService> _logger;

    public ClaimsService(HttpClient httpClient, IConfiguration configuration, ILogger<ClaimsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ClaimSummary>> GetRecentClaimsAsync(int count)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var claims = await _httpClient.GetFromJsonAsync<List<ClaimSummary>>($"{baseUrl}/claims/recent?count={count}");
            return claims ?? new List<ClaimSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimDetails?> GetClaimByIdAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            // Try by ID first, then fall back to claim number lookup
            var response = await _httpClient.GetAsync($"{baseUrl}/claims/{claimId}");
            if (response.IsSuccessStatusCode)
            {
                var claim = await response.Content.ReadFromJsonAsync<ClaimDetails>(JsonOptions);
                return await AddAuditTrailAsync(baseUrl, claim);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var fallback = await _httpClient.GetAsync($"{baseUrl}/claims/number/{claimId}");
                if (fallback.IsSuccessStatusCode)
                {
                    var claim = await fallback.Content.ReadFromJsonAsync<ClaimDetails>(JsonOptions);
                    return await AddAuditTrailAsync(baseUrl, claim);
                }
                if (fallback.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                fallback.EnsureSuccessStatusCode();
            }

            response.EnsureSuccessStatusCode();
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    private async Task<ClaimDetails?> AddAuditTrailAsync(string? baseUrl, ClaimDetails? claim)
    {
        if (claim is null || string.IsNullOrWhiteSpace(claim.ClaimId)) return claim;

        var response = await _httpClient.GetAsync(
            $"{baseUrl}/claims/{Uri.EscapeDataString(claim.ClaimId)}/audit-timeline");
        if (response.StatusCode == HttpStatusCode.NotFound) return claim;

        response.EnsureSuccessStatusCode();
        var timeline = await response.Content.ReadFromJsonAsync<List<ClaimAudit>>(JsonOptions);
        if (timeline is { Count: > 0 }) claim.AuditTrail = timeline;
        return claim;
    }

    public async Task<string?> GetExplanationOfBenefitJsonAsync(string claimId)
    {
        try
        {
            var baseUrl = GetClaimsServiceRootUrl();
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/fhir/ExplanationOfBenefit/{Uri.EscapeDataString(claimId)}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));

            using var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Service configuration invalid: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<MassAdjudicationRunSummary>> GetMassAdjudicationRunsAsync(int limit = 25)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            using var request = CreateMassAdjudicationRequest(
                HttpMethod.Get,
                $"{baseUrl}/mass-adjudication/runs?limit={Math.Clamp(limit, 1, 100)}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await ReadOptionalJsonAsync<List<MassAdjudicationRunSummary>>(response)
                ?? new List<MassAdjudicationRunSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<MassAdjudicationRunSummary?> GetMassAdjudicationRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            using var request = CreateMassAdjudicationRequest(
                HttpMethod.Get,
                $"{baseUrl}/mass-adjudication/runs/{Uri.EscapeDataString(runId)}");
            var response = await _httpClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await ReadOptionalJsonAsync<MassAdjudicationRunSummary>(response);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<MassAdjudicationClaimResult>> GetMassAdjudicationClaimResultsAsync(
        string runId,
        string? outcome = null,
        int limit = 250,
        string? validationStatus = null,
        string? paymentStatus = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var query = $"limit={Math.Clamp(limit, 1, 1000)}";
            if (!string.IsNullOrWhiteSpace(outcome))
            {
                query += $"&outcome={Uri.EscapeDataString(outcome)}";
            }

            if (!string.IsNullOrWhiteSpace(validationStatus))
            {
                query += $"&validationStatus={Uri.EscapeDataString(validationStatus)}";
            }

            if (!string.IsNullOrWhiteSpace(paymentStatus))
            {
                query += $"&paymentStatus={Uri.EscapeDataString(paymentStatus)}";
            }

            using var request = CreateMassAdjudicationRequest(
                HttpMethod.Get,
                $"{baseUrl}/mass-adjudication/runs/{Uri.EscapeDataString(runId)}/claims?{query}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await ReadOptionalJsonAsync<List<MassAdjudicationClaimResult>>(response)
                ?? new List<MassAdjudicationClaimResult>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    private HttpRequestMessage CreateMassAdjudicationRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        var tenantId = _configuration["Services:ClaimsServiceTenantId"]
            ?? _configuration["Authentication:LocalDemo:TenantId"];
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-ID", tenantId);
        }

        return request;
    }

    public async Task<string> SubmitClaimAsync(SubmitClaimRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/claims", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubmitClaimResponse>();
            return result?.ClaimId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimInsightsView> GetClaimInsightsAsync(DateTime from, DateTime to, string? lineOfBusiness = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        var query = $"from={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}"
                    + (string.IsNullOrWhiteSpace(lineOfBusiness) ? "" : $"&lineOfBusiness={Uri.EscapeDataString(lineOfBusiness)}");
        try
        {
            return await _httpClient.GetFromJsonAsync<ClaimInsightsView>($"{baseUrl}/claims/insights?{query}", JsonOptions)
                   ?? new ClaimInsightsView();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<ReportDefinitionView>> GetClaimReportCatalogAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            using var response = await _httpClient.GetAsync($"{baseUrl}/claims/reports");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new List<ReportDefinitionView>();
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<ReportDefinitionView>>(JsonOptions) ?? new List<ReportDefinitionView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ReportRunView> RunClaimReportAsync(string reportId, IReadOnlyDictionary<string, string?> parameters)
    {
        using var response = await GetReportAsync(reportId, parameters, csv: false);
        return await response.Content.ReadFromJsonAsync<ReportRunView>(JsonOptions) ?? new ReportRunView();
    }

    public async Task<ReportFile> DownloadClaimReportCsvAsync(string reportId, IReadOnlyDictionary<string, string?> parameters)
    {
        using var response = await GetReportAsync(reportId, parameters, csv: true);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? $"{reportId}.csv";
        return new ReportFile(fileName, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// GET claims/reports/{id}: a 403 is <see cref="ReportForbiddenException"/>,
    /// a 400 an <see cref="ArgumentException"/> with the service's reason.
    /// </summary>
    private async Task<HttpResponseMessage> GetReportAsync(string reportId, IReadOnlyDictionary<string, string?> parameters, bool csv)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        var query = string.Join("&", parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Append(new KeyValuePair<string, string?>("format", csv ? "csv" : null))
            .Where(p => p.Value is not null)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync($"{baseUrl}/claims/reports/{Uri.EscapeDataString(reportId)}" + (query.Length > 0 ? "?" + query : ""));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new ReportForbiddenException(reportId);
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var reason = await ReadErrorAsync(response);
            response.Dispose();
            throw new ArgumentException(reason);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ServiceUnavailableException("Claims Service", new HttpRequestException($"Report {reportId} failed with {status}."));
        }
        return response;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString() ?? text;
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(text) ? "The report parameters were not accepted." : text;
    }

    public async Task<ClaimSearchResult> SearchClaimsAsync(ClaimSearchRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/claims/search", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ClaimSearchResult>() ?? new ClaimSearchResult();
            result.FillPageTotalsIfMissing();
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<bool> TryRecordAiExaminerAgreementAsync(
        string claimId,
        string agreement,
        string examinerUserId,
        string? notes = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/claims/{Uri.EscapeDataString(claimId)}/ai-examination/agreement",
                new { agreement, examinerUserId, notes });

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "Could not record AI examiner agreement for claim {ClaimId}: HTTP {StatusCode}",
                claimId,
                response.StatusCode);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Could not record AI examiner agreement for claim {ClaimId}",
                claimId);
            return false;
        }
    }

    public async Task<AdjudicationTransparencyData?> GetAdjudicationDataAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/claims/{claimId}/adjudication-detail");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<AdjudicationTransparencyData>(JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<ClaimDuplicateMatch>> GetDuplicateMatchesAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.GetAsync(
                $"{baseUrl}/claims/{Uri.EscapeDataString(claimId)}/duplicate-matches");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new List<ClaimDuplicateMatch>();
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<ClaimDuplicateMatch>>(JsonOptions)
                ?? new List<ClaimDuplicateMatch>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimEditsView?> GetClaimEditsAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/claims/{Uri.EscapeDataString(claimId)}/edits");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClaimEditsView>(JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<ClaimNoteView>> GetClaimNotesAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ClaimNoteView>>(
                       $"{baseUrl}/claims/{Uri.EscapeDataString(claimId)}/notes", JsonOptions)
                   ?? new List<ClaimNoteView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<RecentClaimView>> GetRecentlyViewedClaimsAsync(int limit = 10)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<List<RecentClaimView>>(
                       $"{baseUrl}/claims/recently-viewed?limit={limit}", JsonOptions)
                   ?? new List<RecentClaimView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<ClaimAccessEntry>> GetClaimAccessLogAsync(string claimId, int limit = 200)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<List<ClaimAccessEntry>>(
                       $"{baseUrl}/claims/{Uri.EscapeDataString(claimId)}/access-log?limit={limit}", JsonOptions)
                   ?? new List<ClaimAccessEntry>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<ClaimNoteView> AddClaimNoteAsync(string claimId, string category, string text)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/claims/{Uri.EscapeDataString(claimId)}/notes", new { category, text }, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "You don't have permission to add notes to claims."
                : $"The note wasn't saved ({(int)response.StatusCode}): {detail.Trim('"')}");
        }

        return await response.Content.ReadFromJsonAsync<ClaimNoteView>(JsonOptions)
               ?? throw new InvalidOperationException("The note wasn't saved: empty response.");
    }

    private string GetClaimsServiceRootUrl()
    {
        var baseUrl = (_configuration["Services:ClaimsService"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Services:ClaimsService is not configured.");
        }

        return baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? baseUrl[..^"/api".Length]
            : baseUrl;
    }

    public async Task<EobSearchResponse> SearchClaimsByMemberAsync(string memberId, MemberClaimsFilter filter)
    {
        // The claims-service v1 route lives at the service root
        // ([Route("api/v1/claims")]), while Services:ClaimsService ends in /api.
        var baseUrl = (_configuration["Services:ClaimsService"] ?? string.Empty).TrimEnd('/');
        if (baseUrl.EndsWith("/claims", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/claims".Length];
        if (baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/api".Length];

        var qs = new List<string> { $"memberId={Uri.EscapeDataString(memberId)}" };
        if (filter.ServiceDateFrom.HasValue) qs.Add($"serviceDateFrom={filter.ServiceDateFrom:yyyy-MM-dd}");
        if (filter.ServiceDateTo.HasValue)   qs.Add($"serviceDateTo={filter.ServiceDateTo:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(filter.Status))      qs.Add($"status={filter.Status}");
        if (!string.IsNullOrEmpty(filter.ProviderNPI)) qs.Add($"providerNPI={filter.ProviderNPI}");
        if (!string.IsNullOrEmpty(filter.ClaimType))   qs.Add($"claimType={filter.ClaimType}");
        if (filter.AmountMin.HasValue) qs.Add($"amountMin={filter.AmountMin}");
        if (filter.AmountMax.HasValue) qs.Add($"amountMax={filter.AmountMax}");
        qs.Add($"page={filter.Page}");
        qs.Add($"pageSize={filter.PageSize}");

        try
        {
            return await _httpClient.GetFromJsonAsync<EobSearchResponse>(
                $"{baseUrl}/api/v1/claims?{string.Join("&", qs)}")
                ?? new EobSearchResponse();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    private class SubmitClaimResponse
    {
        public string ClaimId { get; set; } = string.Empty;
    }

    private static async Task<T?> ReadOptionalJsonAsync<T>(HttpResponseMessage response)
    {
        if (response.Content is null)
        {
            return default;
        }

        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body) || string.Equals(body.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }
}
