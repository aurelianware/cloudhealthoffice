using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

public class MemberDocumentService : IMemberDocumentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberDocumentService> _logger;

    public MemberDocumentService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberDocumentService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MemberDocumentSummary>> GetDocumentsAsync(string memberId, string? category = null)
    {
        var baseUrl = GetBaseUrl();
        var url = string.IsNullOrWhiteSpace(category)
            ? $"{baseUrl}/api/v1/members/{Uri.EscapeDataString(memberId)}/documents"
            : $"{baseUrl}/api/v1/members/{Uri.EscapeDataString(memberId)}/documents?category={Uri.EscapeDataString(category)}";

        try
        {
            var documents = await _httpClient.GetFromJsonAsync<List<MemberDocumentSummary>>(url);
            return documents ?? new List<MemberDocumentSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Document Service");
            throw new ServiceUnavailableException("Member Document Service", ex);
        }
    }

    public async Task<MemberDocumentSummary?> GetDocumentAsync(string documentId)
    {
        var baseUrl = GetBaseUrl();
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberDocumentSummary>($"{baseUrl}/api/v1/member-documents/{Uri.EscapeDataString(documentId)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Document Service");
            throw new ServiceUnavailableException("Member Document Service", ex);
        }
    }

    public async Task ToggleLegalHoldAsync(string documentId, bool legalHold)
    {
        var baseUrl = GetBaseUrl();
        try
        {
            var response = await _httpClient.PutAsJsonAsync(
                $"{baseUrl}/api/v1/member-documents/{Uri.EscapeDataString(documentId)}/legal-hold",
                new { legalHold });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Document Service");
            throw new ServiceUnavailableException("Member Document Service", ex);
        }
    }

    public async Task<Stream> DownloadDocumentAsync(string documentId)
    {
        var baseUrl = GetBaseUrl();

        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/api/v1/member-documents/{Uri.EscapeDataString(documentId)}/content");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Document Service");
            throw new ServiceUnavailableException("Member Document Service", ex);
        }
    }

    public async Task<string> UploadDocumentAsync(MemberDocumentUploadRequest request, Stream fileStream)
    {
        var baseUrl = GetBaseUrl();

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(request.MemberId), "MemberId");
            content.Add(new StringContent(request.Category), "Category");
            content.Add(new StringContent(request.Source), "Source");
            content.Add(new StringContent(request.LegalHold.ToString().ToLowerInvariant()), "LegalHold");

            if (!string.IsNullOrWhiteSpace(request.Subcategory))
            {
                content.Add(new StringContent(request.Subcategory), "Subcategory");
            }

            if (!string.IsNullOrWhiteSpace(request.RetentionPolicyId))
            {
                content.Add(new StringContent(request.RetentionPolicyId), "RetentionPolicyId");
            }

            if (!string.IsNullOrWhiteSpace(request.UploadedBy))
            {
                content.Add(new StringContent(request.UploadedBy), "UploadedBy");
            }

            if (!string.IsNullOrWhiteSpace(request.StateCode))
            {
                content.Add(new StringContent(request.StateCode), "StateCode");
            }

            if (request.EffectiveDate.HasValue)
            {
                content.Add(new StringContent(request.EffectiveDate.Value.ToString("O")), "EffectiveDate");
            }

            if (request.ExpirationDate.HasValue)
            {
                content.Add(new StringContent(request.ExpirationDate.Value.ToString("O")), "ExpirationDate");
            }

            if (request.CoverageTerminationDate.HasValue)
            {
                content.Add(new StringContent(request.CoverageTerminationDate.Value.ToString("O")), "CoverageTerminationDate");
            }

            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
            content.Add(streamContent, "file", request.FileName);

            var response = await _httpClient.PostAsync($"{baseUrl}/api/v1/member-documents", content);
            response.EnsureSuccessStatusCode();

            var created = await response.Content.ReadFromJsonAsync<MemberDocumentSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Document Service");
            throw new ServiceUnavailableException("Member Document Service", ex);
        }
    }

    public async Task<JsonDocument?> GetFhirDocumentReferencesAsync(string memberId, string? category = null)
    {
        var baseUrl = GetBaseUrl();
        var url = string.IsNullOrWhiteSpace(category)
            ? $"{baseUrl}/api/v1/members/{Uri.EscapeDataString(memberId)}/fhir/DocumentReference"
            : $"{baseUrl}/api/v1/members/{Uri.EscapeDataString(memberId)}/fhir/DocumentReference?category={Uri.EscapeDataString(category)}";

        try
        {
            return await _httpClient.GetFromJsonAsync<JsonDocument>(url);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Document Service");
            throw new ServiceUnavailableException("Member Document Service", ex);
        }
    }

    private string GetBaseUrl()
    {
        var configured = _configuration["Services:MemberDocumentService"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Services:MemberDocumentService is not configured.");
        }

        return configured.TrimEnd('/');
    }
}
