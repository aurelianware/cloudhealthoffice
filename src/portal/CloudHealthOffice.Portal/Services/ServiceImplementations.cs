using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Identity.Web;
using MongoDB.Driver;
using MongoDB.Bson;

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

    public async Task<ClaimSearchResult> SearchClaimsAsync(ClaimSearchRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/claims/search", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ClaimSearchResult>();
            return result ?? new ClaimSearchResult();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task UpdateClaimStatusAsync(string claimId, string status, string? notes = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var updateRequest = new { status, notes };
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/claims/{claimId}/status", updateRequest);
            response.EnsureSuccessStatusCode();
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
        // The claims-service v1 route lives at the service root (not under
        // /claims), because the controller registers [Route("api/v1/claims")].
        // Build an absolute URL from Services:ClaimsService; strip a trailing
        // /claims if the config value accidentally includes it.
        var baseUrl = (_configuration["Services:ClaimsService"] ?? string.Empty).TrimEnd('/');
        if (baseUrl.EndsWith("/claims", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/claims".Length];

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

public class EdiTransactionsService : IEdiTransactionsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EdiTransactionsService> _logger;

    public EdiTransactionsService(HttpClient httpClient, IConfiguration configuration, ILogger<EdiTransactionsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Enrollment834Record>> GetEnrollment834TransactionsAsync(int limit = 100)
    {
        var baseUrl = _configuration["Services:EnrollmentImportService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>(
                $"{baseUrl}/v1/enrollment/transactions/recent?limit={Math.Clamp(limit, 1, 500)}", JsonOptions);
            return records ?? new List<Enrollment834Record>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Enrollment Import Service");
            throw new ServiceUnavailableException("Enrollment Import Service", ex);
        }
    }

    public async Task<List<ClaimImportTransactionRecord>> GetClaimImportTransactionsAsync(int limit = 100)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<ClaimImportTransactionRecord>>(
                $"{baseUrl}/v1/claims/import-transactions?limit={Math.Clamp(limit, 1, 500)}", JsonOptions);
            return records ?? new List<ClaimImportTransactionRecord>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<EnrollmentImportRunRecord>> GetEnrollmentImportRunsAsync(int limit = 100)
    {
        var baseUrl = _configuration["Services:EnrollmentImportService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<EnrollmentImportRunRecord>>(
                $"{baseUrl}/v1/enrollment/import-runs?limit={Math.Clamp(limit, 1, 500)}", JsonOptions);
            return records ?? new List<EnrollmentImportRunRecord>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Enrollment Import Service");
            throw new ServiceUnavailableException("Enrollment Import Service", ex);
        }
    }
}

public sealed class FlexibleClaimTypeJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number when reader.TryGetInt32(out var value) => value switch
            {
                1 => "Professional",
                2 => "Institutional",
                3 => "Dental",
                _ => value.ToString()
            },
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            JsonTokenType.Null => string.Empty,
            _ => JsonDocument.ParseValue(ref reader).RootElement.ToString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class FlexibleClaimStatusJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number when reader.TryGetInt32(out var value) => value switch
            {
                1 => "Submitted",
                2 => "Received",
                3 => "InAdjudication",
                4 => "Pended",
                5 => "Approved",
                6 => "Denied",
                7 => "Paid",
                8 => "Voided",
                9 => "PartiallyPaid",
                _ => value.ToString()
            },
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            JsonTokenType.Null => string.Empty,
            _ => JsonDocument.ParseValue(ref reader).RootElement.ToString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class FlexibleDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetDecimal(out var value) => value,
            JsonTokenType.Number => Convert.ToDecimal(reader.GetDouble()),
            JsonTokenType.String when decimal.TryParse(
                reader.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) => value,
            JsonTokenType.String => 0m,
            JsonTokenType.Null => 0m,
            JsonTokenType.True => 1m,
            JsonTokenType.False => 0m,
            _ => 0m
        };
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

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

public class MemberService : IMemberService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberService> _logger;

    public MemberService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MemberSummary>> SearchMembersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var members = await _httpClient.GetFromJsonAsync<List<MemberSummary>>($"{baseUrl}/members/search?q={Uri.EscapeDataString(searchTerm)}");
            return members ?? new List<MemberSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberDetails?> GetMemberByIdAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberDetails>($"{baseUrl}/members/{memberId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberPcp?> GetMemberPcpAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<MemberPcp>($"{baseUrl}/members/{memberId}/pcp");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<PcpAssignmentOutcome> AssignPcpAsync(AssignPcpRequest request)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/members/{request.MemberId}/pcp", request);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                PcpValidationProblem? problem = null;
                try
                {
                    problem = await response.Content.ReadFromJsonAsync<PcpValidationProblem>();
                }
                catch (JsonException) { /* malformed body → fall through to default */ }

                return new PcpAssignmentOutcome
                {
                    ValidationError = problem ?? new PcpValidationProblem
                    {
                        Code = "VALIDATION_FAILED",
                        Message = "PCP assignment was rejected."
                    }
                };
            }
            response.EnsureSuccessStatusCode();
            var pcp = await response.Content.ReadFromJsonAsync<MemberPcp>();
            if (pcp is null)
            {
                // 2xx with empty/invalid body is a contract break — surface as
                // unavailable so the UI gets a deterministic failure path
                // rather than a warning with an empty message.
                throw new ServiceUnavailableException("Member Service",
                    new HttpRequestException("Member Service returned an empty PCP assignment response."));
            }
            return new PcpAssignmentOutcome { Pcp = pcp };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<PcpAssignmentHistoryItem>> GetMemberPcpHistoryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var history = await _httpClient.GetFromJsonAsync<List<PcpAssignmentHistoryItem>>(
                $"{baseUrl}/members/{memberId}/pcp/history");
            return history ?? new List<PcpAssignmentHistoryItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<CoverageHistoryEvent>> GetCoverageHistoryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var history = await _httpClient.GetFromJsonAsync<List<CoverageHistoryEvent>>($"{baseUrl}/members/{memberId}/coverage-history");
            return history ?? new List<CoverageHistoryEvent>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<Enrollment834Record>> GetMember834TransactionsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var records = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>($"{baseUrl}/members/{memberId}/834-transactions");
            return records ?? new List<Enrollment834Record>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<EnrollmentEventPage> GetEnrollmentEventsAsync(string memberId, EnrollmentEventFilter filter)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var qs = new List<string> { $"limit={Math.Clamp(filter.Limit, 1, 200)}" };
        if (!string.IsNullOrWhiteSpace(filter.Type)) qs.Add($"type={Uri.EscapeDataString(filter.Type)}");
        if (filter.From.HasValue) qs.Add($"from={Uri.EscapeDataString(filter.From.Value.ToString("o"))}");
        if (filter.To.HasValue) qs.Add($"to={Uri.EscapeDataString(filter.To.Value.ToString("o"))}");
        if (!string.IsNullOrWhiteSpace(filter.ContinuationToken))
            qs.Add($"continuationToken={Uri.EscapeDataString(filter.ContinuationToken)}");

        try
        {
            var url = $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/enrollment-events?{string.Join("&", qs)}";
            var page = await _httpClient.GetFromJsonAsync<EnrollmentEventPage>(url);
            return page ?? new EnrollmentEventPage();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task TerminateEnrollmentAsync(TerminateEnrollmentRequest request)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/members/{request.MemberId}/terminate", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAccumulators> GetAccumulatorsAsync(string memberId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var accums = await _httpClient.GetFromJsonAsync<MemberAccumulators>($"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/accumulators");
            return accums ?? new MemberAccumulators();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}

public class MemberAlertService : IMemberAlertService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberAlertService> _logger;

    public MemberAlertService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberAlertService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<MemberAlertView>> ListAsync(string memberId, bool activeOnly)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var url = $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/alerts";
        if (activeOnly) url += "?status=active";
        try
        {
            var page = await _httpClient.GetFromJsonAsync<MemberAlertListEnvelope>(url);
            return page?.Items ?? new List<MemberAlertView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAlertView?> CreateAsync(string memberId, CreateMemberAlertPayload payload)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/alerts", payload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberAlertView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberAlertView?> EndAsync(string memberId, string alertId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/alerts/{Uri.EscapeDataString(alertId)}/end",
                new { });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberAlertView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    private sealed class MemberAlertListEnvelope
    {
        public List<MemberAlertView> Items { get; set; } = new();
    }
}

public class MemberNoteService : IMemberNoteService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MemberNoteService> _logger;

    public MemberNoteService(HttpClient httpClient, IConfiguration configuration, ILogger<MemberNoteService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<MemberNotePage> ListAsync(string memberId, MemberNoteFilter filter)
    {
        var baseUrl = _configuration["Services:MemberService"];
        var qs = new List<string> { $"pageSize={Math.Clamp(filter.Limit, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(filter.Category)) qs.Add($"category={Uri.EscapeDataString(filter.Category)}");
        if (!string.IsNullOrWhiteSpace(filter.ContinuationToken))
            qs.Add($"continuationToken={Uri.EscapeDataString(filter.ContinuationToken)}");

        try
        {
            var url = $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/notes?{string.Join("&", qs)}";
            var page = await _httpClient.GetFromJsonAsync<MemberNotePage>(url);
            return page ?? new MemberNotePage();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<MemberNoteView?> CreateAsync(string memberId, CreateMemberNotePayload payload)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/members/{Uri.EscapeDataString(memberId)}/notes", payload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberNoteView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}

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

public class AuthorizationService : IAuthorizationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthorizationService> _logger;
    private readonly ITokenAcquisition? _tokenAcquisition;

    public AuthorizationService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AuthorizationService> logger,
        ITokenAcquisition? tokenAcquisition = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
    }

    public async Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            await SetBearerTokenAsync();
            var url = string.IsNullOrEmpty(memberId)
                ? $"{baseUrl}/authorizations/search"
                : $"{baseUrl}/authorizations/search?memberId={memberId}";
            var auths = await _httpClient.GetFromJsonAsync<List<AuthorizationSummary>>(url);
            return auths ?? new List<AuthorizationSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            await SetBearerTokenAsync();
            return await _httpClient.GetFromJsonAsync<AuthorizationDetails>($"{baseUrl}/authorizations/{authorizationId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            await SetBearerTokenAsync();
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/authorizations", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SubmitAuthorizationResponse>();
            return result?.AuthorizationId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    private async Task SetBearerTokenAsync()
    {
        if (_tokenAcquisition is null || IsLocalDemoAuth())
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            return;
        }

        var scopes = new[] { "api://cfada1ac-f251-48ea-9330-39212aa4c862/Authorization.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private bool IsLocalDemoAuth()
        => string.Equals(
            _configuration["Authentication:Mode"],
            "LocalDemo",
            StringComparison.OrdinalIgnoreCase);

    private class SubmitAuthorizationResponse
    {
        public string AuthorizationId { get; set; } = string.Empty;
    }
}

public class ProviderService : IProviderService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderService> _logger;

    public ProviderService(HttpClient httpClient, IConfiguration configuration, ILogger<ProviderService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var providers = await _httpClient.GetFromJsonAsync<List<ProviderSummary>>($"{baseUrl}/providers/search?q={searchTerm}");
            return providers ?? new List<ProviderSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<List<ProviderListItem>> SearchProvidersAsync(string? specialty = null, string? networkStatus = null, string? searchTerm = null)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var query = $"{baseUrl}/providers/list?";
            if (!string.IsNullOrEmpty(specialty))
                query += $"specialty={specialty}&";
            if (!string.IsNullOrEmpty(networkStatus))
                query += $"networkStatus={networkStatus}&";
            if (!string.IsNullOrEmpty(searchTerm))
                query += $"search={searchTerm}";

            var providers = await _httpClient.GetFromJsonAsync<List<ProviderListItem>>(query);
            return providers ?? new List<ProviderListItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderDetails?> GetProviderByIdAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<ProviderDetails>($"{baseUrl}/providers/{providerId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<string> CreateProviderAsync(CreateProviderRequest request)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/providers", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<dynamic>();
            return result?.providerId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task UpdateProviderAsync(string providerId, UpdateProviderRequest request)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/providers/{providerId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderNetworkInfo?> GetNetworkAsync(string networkId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{baseUrl}/v1/networks/{Uri.EscapeDataString(networkId)}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderNetworkInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderNetworkRoster?> GetNetworkRosterAsync(
        string networkId, DateTime asOfDate, int pageSize = 25)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var url = $"{baseUrl}/v1/networks/{Uri.EscapeDataString(networkId)}/roster" +
                      $"?asOfDate={asOfDate:yyyy-MM-dd}&pageSize={Math.Clamp(pageSize, 1, 200)}";
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderNetworkRoster>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderNetworkMembership?> GetNetworkMembershipAsync(
        string networkId, string npi, DateTime asOfDate)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var url = $"{baseUrl}/v1/networks/{Uri.EscapeDataString(networkId)}/members/" +
                      $"{Uri.EscapeDataString(npi)}?asOf={asOfDate:yyyy-MM-dd}";
            using var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderNetworkMembership>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<ProviderIntegrityRefreshResult?> RefreshProviderVerificationAsync(string providerId)
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            // Capability 5.10 — on-demand refresh routes through the
            // existing 5.4.5 endpoint (POST /providers/{id}/verification/refresh
            // on ProvidersController, NOT IntegrityProjectionAdminController).
            var response = await _httpClient.PostAsync(
                $"{baseUrl}/providers/{providerId}/verification/refresh",
                content: null);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ProviderIntegrityRefreshResult>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }

    public async Task<List<string>> GetSpecialtiesAsync()
    {
        var baseUrl = _configuration["Services:ProviderService"];
        try
        {
            var specialties = await _httpClient.GetFromJsonAsync<List<string>>($"{baseUrl}/providers/specialties");
            return specialties ?? new List<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Provider Service");
            throw new ServiceUnavailableException("Provider Service", ex);
        }
    }
}


public class BenefitPlanService : IBenefitPlanService
{
    private static readonly JsonSerializerOptions BenefitPlanJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BenefitPlanService> _logger;

    public BenefitPlanService(HttpClient httpClient, IConfiguration configuration, ILogger<BenefitPlanService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<BenefitPlan>> GetBenefitPlansAsync()
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlan>>($"{baseUrl}/v1/plans");
            return plans ?? new List<BenefitPlan>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<BenefitPlanListItem>> SearchBenefitPlansAsync(string? sponsorId = null, string? productType = null)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var query = $"{baseUrl}/v1/plans?";
            if (!string.IsNullOrEmpty(sponsorId))
                query += $"payer={sponsorId}&";
            if (!string.IsNullOrEmpty(productType))
                query += $"planType={productType}";

            var plans = await _httpClient.GetFromJsonAsync<List<BenefitPlanApiResponse>>(
                query,
                BenefitPlanJsonOptions);
            return plans?.Select(MapListItem).ToList() ?? new List<BenefitPlanListItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var plan = await _httpClient.GetFromJsonAsync<BenefitPlanApiResponse>(
                $"{baseUrl}/v1/plans/{Uri.EscapeDataString(planId)}",
                BenefitPlanJsonOptions);
            return plan == null ? null : MapDetails(plan);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<string> CreateBenefitPlanAsync(CreateBenefitPlanRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            // Map portal fields to service model fields
            var payload = new
            {
                tenantId = "placeholder",  // overwritten by service from X-Tenant-ID header
                planId = $"PLAN-{Guid.NewGuid().ToString()[..8].ToUpper()}",
                planName = request.PlanName,
                payer = request.SponsorId,
                planType = request.ProductType,
                metalLevel = request.MetalTier,
                effectiveDate = request.EffectiveDate == default ? DateTime.UtcNow : request.EffectiveDate,
                lineOfBusiness = "Commercial",
                networkTiers = string.IsNullOrWhiteSpace(request.Network)
                    ? Array.Empty<object>()
                    : new[]
                    {
                        new
                        {
                            tierName = request.Network,
                            tierLevel = 1,
                            networkId = request.Network
                        }
                    },
                costSharing = new
                {
                    individualDeductible = request.IndividualDeductible,
                    familyDeductible = request.FamilyDeductible,
                    individualOutOfPocketMax = request.IndividualOOPMax,
                    familyOutOfPocketMax = request.FamilyOOPMax,
                    coinsurance = request.Coinsurance,
                    monthlyPremium = request.MonthlyPremium
                }
            };
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/v1/plans", payload);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateBenefitPlanResponse>();
            return result?.PlanId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateBenefitPlanAsync(string planId, UpdateBenefitPlanRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/v1/plans/{planId}", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public Task AddBenefitAsync(string planId, UpsertPlanBenefitRequest request)
        => WriteBenefitAsync(HttpMethod.Post, planId, benefitId: null, request);

    public Task UpdateBenefitAsync(
        string planId,
        string benefitId,
        UpsertPlanBenefitRequest request)
        => WriteBenefitAsync(HttpMethod.Put, planId, benefitId, request);

    public async Task ReplaceNetworkTiersAsync(
        string planId,
        IReadOnlyList<PlanNetworkTier> networkTiers)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var url = $"{baseUrl}/v1/plans/{Uri.EscapeDataString(planId)}/network-tiers";
        var payload = networkTiers.Select(tier => new
        {
            id = tier.Id,
            tierName = tier.TierName.Trim(),
            tierLevel = tier.TierLevel,
            networkId = tier.NetworkId.Trim(),
        });

        try
        {
            var response = await _httpClient.PutAsJsonAsync(url, payload);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    private async Task WriteBenefitAsync(
        HttpMethod method,
        string planId,
        string? benefitId,
        UpsertPlanBenefitRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var escapedPlanId = Uri.EscapeDataString(planId);
        var url = benefitId == null
            ? $"{baseUrl}/v1/plans/{escapedPlanId}/benefits"
            : $"{baseUrl}/v1/plans/{escapedPlanId}/benefits/{Uri.EscapeDataString(benefitId)}";
        var payload = new
        {
            benefitType = request.BenefitType,
            serviceCategory = request.ServiceCategory,
            description = request.Description,
            isCovered = request.IsCovered,
            cptCodes = request.CptCodes,
            inNetworkCopay = request.InNetworkCopay,
            outNetworkCopay = request.OutNetworkCopay,
            inNetworkCoinsurance = NormalizeApiPercent(request.InNetworkCoinsurancePercent),
            outNetworkCoinsurance = NormalizeApiPercent(request.OutNetworkCoinsurancePercent),
            deductibleApplies = request.DeductibleApplies,
            oopApplies = request.OopApplies,
            priorAuthRequired = request.PriorAuthRequired,
            visitLimit = request.VisitLimit,
            visitLimitPeriod = request.VisitLimitPeriod,
        };

        try
        {
            using var message = new HttpRequestMessage(method, url)
            {
                Content = JsonContent.Create(payload),
            };
            using var response = await _httpClient.SendAsync(message);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<BenefitItem>> GetAvailableBenefitsAsync()
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var benefits = await _httpClient.GetFromJsonAsync<List<BenefitItem>>($"{baseUrl}/v1/plans/benefits");
            return benefits ?? new List<BenefitItem>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<List<ServiceBenefitRule>> GetServiceBenefitRulesAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<ServiceBenefitRule>>($"{baseUrl}/v1/plans/{planId}/service-rules");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateServiceBenefitRulesAsync(UpdateServiceBenefitRulesRequest request)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/v1/plans/{request.PlanId}/service-rules", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<AccumulatorConfiguration?> GetAccumulatorConfigAsync(string planId)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AccumulatorConfiguration>($"{baseUrl}/v1/plans/{planId}/accumulators");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task UpdateAccumulatorConfigAsync(string planId, AccumulatorConfiguration config)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/v1/plans/{planId}/accumulators", config);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    public async Task<MemberBenefitView?> GetMemberViewAsync(string planId, DateTime serviceDate)
    {
        var baseUrl = _configuration["Services:BenefitPlanService"];
        var url = $"{baseUrl}/v1/benefit-plans/{Uri.EscapeDataString(planId)}/member-view?serviceDate={serviceDate:yyyy-MM-dd}";
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberBenefitView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Benefit Plan Service");
            throw new ServiceUnavailableException("Benefit Plan Service", ex);
        }
    }

    private class CreateBenefitPlanResponse
    {
        public string PlanId { get; set; } = string.Empty;
    }

    private static BenefitPlanListItem MapListItem(BenefitPlanApiResponse plan)
    {
        var primaryNetwork = plan.NetworkTiers
            .OrderBy(tier => tier.TierLevel)
            .FirstOrDefault();

        return new BenefitPlanListItem
        {
            PlanId = plan.PlanId,
            PlanName = plan.PlanName,
            SponsorId = plan.Payer,
            SponsorName = plan.Payer,
            ProductType = plan.PlanType,
            Network = primaryNetwork?.NetworkId ?? primaryNetwork?.TierName ?? string.Empty,
            EnrolledMembers = 0,
            AssignedBenefits = plan.Benefits.Count(IsCovered),
            MonthlyPremium = plan.CostSharing.MonthlyPremium,
            Status = MapStatus(plan),
            EffectiveDate = plan.EffectiveDate,
            TerminationDate = plan.TerminationDate
        };
    }

    private static BenefitPlanDetails MapDetails(BenefitPlanApiResponse plan)
    {
        var listItem = MapListItem(plan);
        return new BenefitPlanDetails
        {
            PlanId = listItem.PlanId,
            VersionId = plan.VersionId,
            VersionNumber = plan.VersionNumber,
            VersionState = plan.VersionState,
            PlanName = listItem.PlanName,
            SponsorId = listItem.SponsorId,
            SponsorName = listItem.SponsorName,
            ProductType = listItem.ProductType,
            Network = listItem.Network,
            EnrolledMembers = listItem.EnrolledMembers,
            AssignedBenefits = listItem.AssignedBenefits,
            MonthlyPremium = listItem.MonthlyPremium,
            Status = listItem.Status,
            EffectiveDate = listItem.EffectiveDate,
            TerminationDate = listItem.TerminationDate,
            MetalTier = plan.MetalLevel ?? string.Empty,
            IndividualDeductible = plan.CostSharing.IndividualDeductible,
            FamilyDeductible = plan.CostSharing.FamilyDeductible,
            IndividualOOPMax = plan.CostSharing.IndividualOutOfPocketMax,
            FamilyOOPMax = plan.CostSharing.FamilyOutOfPocketMax,
            Coinsurance = plan.CostSharing.Coinsurance,
            PlanYear = plan.EffectiveDate.Year.ToString(),
            Benefits = plan.Benefits.Where(IsCovered).Select(MapBenefit).ToList(),
            Exclusions = plan.Benefits.Where(benefit => !IsCovered(benefit)).Select(MapBenefit).ToList(),
            NetworkTiers = plan.NetworkTiers
                .OrderBy(tier => tier.TierLevel)
                .Select(tier => new PlanNetworkTier
                {
                    Id = tier.Id,
                    TierName = tier.TierName,
                    TierLevel = tier.TierLevel,
                    NetworkId = tier.NetworkId ?? string.Empty,
                }).ToList()
        };
    }

    private static bool IsCovered(BenefitPlanApiBenefit benefit) => benefit.IsCovered != false;

    private static PlanBenefit MapBenefit(BenefitPlanApiBenefit benefit)
    {
        var coinsurance = benefit.InNetworkCoinsurance ?? benefit.CoinsurancePercentage;
        var normalizedCoinsurance = NormalizePercent(coinsurance);

        return new PlanBenefit
        {
            BenefitId = benefit.Id,
            BenefitType = string.IsNullOrWhiteSpace(benefit.BenefitType) ? "medical" : benefit.BenefitType,
            ServiceCategory = benefit.ServiceCategory,
            Description = benefit.Description,
            IsCovered = IsCovered(benefit),
            ServiceType = string.IsNullOrWhiteSpace(benefit.Description)
                ? benefit.ServiceCategory
                : benefit.Description,
            Category = string.IsNullOrWhiteSpace(benefit.BenefitType)
                ? "Medical"
                : char.ToUpperInvariant(benefit.BenefitType[0]) + benefit.BenefitType[1..],
            Copay = benefit.InNetworkCopay ?? benefit.CopayAmount,
            CoinsurancePercent = normalizedCoinsurance,
            OutNetworkCopay = benefit.OutNetworkCopay,
            OutNetworkCoinsurancePercent = NormalizePercent(benefit.OutNetworkCoinsurance),
            CoveragePercent = normalizedCoinsurance.HasValue ? 100m - normalizedCoinsurance.Value : null,
            AnnualLimit = benefit.VisitLimit,
            VisitLimitPeriod = benefit.VisitLimitPeriod,
            DeductibleApplies = benefit.DeductibleApplies,
            OopApplies = benefit.OopApplies,
            PriorAuthRequired = benefit.PriorAuthRequired || benefit.RequiresPriorAuth,
            CptCodes = benefit.CptCodes,
        };
    }

    private static decimal? NormalizeApiPercent(decimal? value)
        => value.HasValue ? value.Value / 100m : null;

    private static decimal? NormalizePercent(decimal? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value is >= 0m and <= 1m ? value.Value * 100m : value.Value;
    }

    private static string MapStatus(BenefitPlanApiResponse plan) => plan.VersionState switch
    {
        "Draft" => "Pending",
        "Published" => plan.IsActive ? "Active" : "Inactive",
        "Superseded" => "Inactive",
        _ => plan.IsActive ? "Active" : "Inactive"
    };

    private sealed class BenefitPlanApiResponse
    {
        public string PlanId { get; set; } = string.Empty;
        public string VersionId { get; set; } = string.Empty;
        public int VersionNumber { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public string Payer { get; set; } = string.Empty;
        public string PlanType { get; set; } = string.Empty;
        public string? MetalLevel { get; set; }
        public DateTime EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public bool IsActive { get; set; }
        public string VersionState { get; set; } = string.Empty;
        public List<BenefitPlanApiNetworkTier> NetworkTiers { get; set; } = new();
        public BenefitPlanApiCostSharing CostSharing { get; set; } = new();
        public List<BenefitPlanApiBenefit> Benefits { get; set; } = new();
    }

    private sealed class BenefitPlanApiNetworkTier
    {
        public string Id { get; set; } = string.Empty;
        public string TierName { get; set; } = string.Empty;
        public int TierLevel { get; set; }
        public string? NetworkId { get; set; }
    }

    private sealed class BenefitPlanApiCostSharing
    {
        public decimal IndividualDeductible { get; set; }
        public decimal FamilyDeductible { get; set; }
        public decimal IndividualOutOfPocketMax { get; set; }
        public decimal FamilyOutOfPocketMax { get; set; }
        public decimal Coinsurance { get; set; }
        public decimal MonthlyPremium { get; set; }
    }

    private sealed class BenefitPlanApiBenefit
    {
        public string Id { get; set; } = string.Empty;
        public string BenefitType { get; set; } = string.Empty;
        public string ServiceCategory { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool? IsCovered { get; set; }
        public List<string> CptCodes { get; set; } = new();
        public decimal? InNetworkCopay { get; set; }
        public decimal? OutNetworkCopay { get; set; }
        public decimal? InNetworkCoinsurance { get; set; }
        public decimal? OutNetworkCoinsurance { get; set; }
        public decimal? CopayAmount { get; set; }
        public decimal? CoinsurancePercentage { get; set; }
        public bool DeductibleApplies { get; set; }
        public bool OopApplies { get; set; }
        public bool PriorAuthRequired { get; set; }
        public bool RequiresPriorAuth { get; set; }
        public int? VisitLimit { get; set; }
        public string? VisitLimitPeriod { get; set; }
    }
}

public class WorkflowService : IWorkflowService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public WorkflowService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<WorkflowRun>> GetWorkflowRunsAsync(int limit = 20)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?limit={limit}");
            return workflows ?? new List<WorkflowRun>();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<WorkflowDetails?> GetWorkflowDetailsAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            return await _httpClient.GetFromJsonAsync<WorkflowDetails>($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<List<WorkflowRun>> GetActiveWorkflowsAsync()
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var workflows = await _httpClient.GetFromJsonAsync<List<WorkflowRun>>($"{baseUrl}/api/v1/workflows/cho-workflows?phase=Running");
            return workflows ?? new List<WorkflowRun>();
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }

    public async Task<bool> RetriggerWorkflowAsync(string workflowId)
    {
        var baseUrl = _configuration["Services:ArgoWorkflows"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/api/v1/workflows/cho-workflows/{workflowId}/retry", null);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceUnavailableException("Argo Workflows", ex);
        }
    }
}

// TEMPORARY: Replace when Prometheus is deployed.
// MetricsService retains mock data until real Prometheus metrics are wired up.
public class MetricsService : IMetricsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsService> _logger;

    public MetricsService(HttpClient httpClient, IConfiguration configuration, ILogger<MetricsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DashboardMetrics> GetDashboardMetricsAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<ClaimsSummaryResponse>($"{baseUrl}/claims/summary");
            if (summary == null) return new DashboardMetrics();

            var total = summary.TotalClaims;
            return new DashboardMetrics
            {
                TotalClaims = total,
                ClaimsTrend = 0,
                ApprovalRate = total > 0 ? (double)summary.ApprovedClaims / total : 0,
                AvgProcessingTimeMs = (int)summary.AverageProcessingDays * 24 * 60,
                TotalPayerAmount = summary.TotalPaidAmount,
                ApprovedClaims = summary.ApprovedClaims,
                DeniedClaims = summary.DeniedClaims,
                PendingClaims = summary.PendedClaims
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    private class ClaimsSummaryResponse
    {
        public int TotalClaims { get; set; }
        public int ApprovedClaims { get; set; }
        public int DeniedClaims { get; set; }
        public int PendedClaims { get; set; }
        public int PaidClaims { get; set; }
        public decimal TotalChargeAmount { get; set; }
        public decimal TotalAllowedAmount { get; set; }
        public decimal TotalPaidAmount { get; set; }
        public decimal AverageProcessingDays { get; set; }
        public decimal ApprovalRate { get; set; }
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<OperationalAlerts> GetOperationalAlertsAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var alerts = await _httpClient.GetFromJsonAsync<OperationalAlerts>($"{baseUrl}/metrics/operational-alerts");
            return alerts ?? new OperationalAlerts();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    // TEMPORARY: Replace when Prometheus is deployed.
    public async Task<EdiVolumeSummary> GetTodayEdiVolumeAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var volume = await _httpClient.GetFromJsonAsync<EdiVolumeSummary>($"{baseUrl}/metrics/edi-volume/today");
            return volume ?? new EdiVolumeSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

}

public class AttachmentService : IAttachmentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AttachmentService> _logger;
    private readonly ITokenAcquisition? _tokenAcquisition;

    public AttachmentService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AttachmentService> logger,
        ITokenAcquisition? tokenAcquisition = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _tokenAcquisition = tokenAcquisition;
    }

    public async Task<List<AttachmentInfo>> GetAttachmentsAsync(string authorizationId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            await SetBearerTokenAsync();
            var attachments = await _httpClient.GetFromJsonAsync<List<AttachmentInfo>>($"{baseUrl}/attachments/authorization/{authorizationId}");
            return attachments ?? new List<AttachmentInfo>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task<string> UploadAttachmentAsync(string authorizationId, Stream fileStream, string fileName, string contentType)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            await SetBearerTokenAsync();
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(streamContent, "file", fileName);
            content.Add(new StringContent(authorizationId), "authorizationId");

            var response = await _httpClient.PostAsync($"{baseUrl}/attachments/upload", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<UploadAttachmentResponse>();
            return result?.AttachmentId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task<Stream> DownloadAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            await SetBearerTokenAsync();
            var response = await _httpClient.GetAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    public async Task DeleteAttachmentAsync(string authorizationId, string attachmentId)
    {
        var baseUrl = _configuration["Services:AttachmentService"];
        try
        {
            var response = await _httpClient.DeleteAsync($"{baseUrl}/attachments/{authorizationId}/{attachmentId}");
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Attachment Service");
            throw new ServiceUnavailableException("Attachment Service", ex);
        }
    }

    private async Task SetBearerTokenAsync()
    {
        if (_tokenAcquisition is null || IsLocalDemoAuth())
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            return;
        }

        var scopes = new[] { "api://cfada1ac-f251-48ea-9330-39212aa4c862/Attachments.ReadWrite" };
        var accessToken = await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private bool IsLocalDemoAuth()
        => string.Equals(
            _configuration["Authentication:Mode"],
            "LocalDemo",
            StringComparison.OrdinalIgnoreCase);

    private class UploadAttachmentResponse
    {
        public string AttachmentId { get; set; } = string.Empty;
    }
}

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

    public async Task<CodeUsageStats> GetCodeUsageStatsAsync(string codeSystem, string code)
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

public class TenantService : ITenantService
{
    private readonly IMongoCollection<TenantSubscription> _tenantsCollection;
    private readonly IMongoCollection<BsonDocument> _membersCollection;
    private readonly IMongoCollection<BsonDocument> _tenantUsersCollection;
    private readonly ILogger<TenantService> _logger;

    public TenantService(IMongoClient mongoClient, IConfiguration configuration, ILogger<TenantService> logger)
    {
        _logger = logger;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _tenantsCollection = db.GetCollection<TenantSubscription>(
            configuration["MongoDB:TenantsCollection"] ?? "Tenants");
        _tenantUsersCollection = db.GetCollection<BsonDocument>("TenantUsers");
        _membersCollection = db.GetCollection<BsonDocument>(
            configuration["MongoDB:MembersCollection"] ?? "Members");
    }

    public async Task<TenantSubscription?> GetSubscriptionByAzureTenantIdAsync(string azureTenantId)
    {
        _logger.LogInformation("Looking up subscription for Azure Tenant ID: {TenantId}", azureTenantId);

        if (string.IsNullOrEmpty(azureTenantId) || azureTenantId == "common")
            return null;

        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var tenant = await _tenantsCollection.Find(filter).FirstOrDefaultAsync();

        if (tenant != null)
            _logger.LogInformation("Found subscription for tenant {TenantId}: {OrgName} ({Status})",
                azureTenantId, tenant.OrganizationName, tenant.SubscriptionStatus);
        else
            _logger.LogInformation("No subscription found for Azure Tenant ID: {TenantId}", azureTenantId);

        return tenant;
    }

    public async Task<TenantSubscription?> GetDemoTenantAsync()
    {
        try
        {
            _logger.LogInformation("Fetching demo tenant");

            var filter = Builders<TenantSubscription>.Filter.Eq(t => t.IsDemo, true);
            var demoTenant = await _tenantsCollection.Find(filter).FirstOrDefaultAsync();

            if (demoTenant != null)
                return demoTenant;

            _logger.LogWarning("No demo tenant found in MongoDB, returning default");
            return new TenantSubscription
            {
                TenantId = "demo-tenant",
                AzureTenantId = "demo",
                OrganizationName = "Demo Health Plan",
                SubscriptionStatus = "Active",
                Tier = "enterprise",
                IsDemo = true,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                UpdatedAt = DateTime.UtcNow,
                AdminEmails = new List<string> { "demo@cloudhealthoffice.com" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching demo tenant from MongoDB");
            return new TenantSubscription
            {
                TenantId = "demo-tenant",
                AzureTenantId = "demo",
                OrganizationName = "Demo Health Plan",
                SubscriptionStatus = "Active",
                Tier = "enterprise",
                IsDemo = true,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                UpdatedAt = DateTime.UtcNow,
                AdminEmails = new List<string> { "demo@cloudhealthoffice.com" }
            };
        }
    }

    public async Task<bool> IsMemberOfTenantAsync(string azureTenantId, string userEmail)
    {
        try
        {
            _logger.LogInformation("Checking if {Email} is member of tenant {TenantId}", userEmail, azureTenantId);

            if (string.IsNullOrEmpty(userEmail) || string.IsNullOrEmpty(azureTenantId))
                return false;

            var tenant = await GetSubscriptionByAzureTenantIdAsync(azureTenantId);
            if (tenant == null)
            {
                _logger.LogWarning("Tenant not found for Azure Tenant ID: {TenantId}", azureTenantId);
                return false;
            }

            if (tenant.AdminEmails.Contains(userEmail, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("User {Email} is admin for tenant {TenantId}", userEmail, azureTenantId);
                return true;
            }

            // Check TenantUsers collection (RBAC users managed by tenant-service)
            var tenantUserFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenant.TenantId),
                Builders<BsonDocument>.Filter.Eq("emailNormalized", userEmail.ToLowerInvariant()),
                Builders<BsonDocument>.Filter.Eq("status", "Active"));
            var tenantUserCount = await _tenantUsersCollection.CountDocumentsAsync(tenantUserFilter);
            if (tenantUserCount > 0)
            {
                _logger.LogInformation("User {Email} found in TenantUsers for tenant {TenantId}", userEmail, azureTenantId);
                return true;
            }

            // Check Members collection (legacy member records)
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("tenantId", tenant.TenantId),
                Builders<BsonDocument>.Filter.Eq("email", userEmail.ToLowerInvariant()));
            var count = await _membersCollection.CountDocumentsAsync(filter);
            var hasMember = count > 0;

            _logger.LogInformation("User {Email} member status for tenant {TenantId}: {IsMember}",
                userEmail, azureTenantId, hasMember);
            return hasMember;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking membership for {Email} in tenant {TenantId}", userEmail, azureTenantId);
            return false;
        }
    }

    public async Task<string> CreateTenantAsync(CreateTenantRequest request)
    {
        try
        {
            var tenantId = $"tenant-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;

            // Merge AdminEmail (from signup) into AdminEmails list
            var adminEmails = request.AdminEmails ?? new List<string>();
            if (!string.IsNullOrWhiteSpace(request.AdminEmail) && !adminEmails.Contains(request.AdminEmail))
                adminEmails.Add(request.AdminEmail);

            var tenant = new TenantSubscription
            {
                TenantId = tenantId,
                AzureTenantId = request.AzureTenantId,
                OrganizationName = request.OrganizationName,
                SubscriptionStatus = request.SubscriptionStatus,
                Tier = request.Tier,
                IsDemo = request.IsDemo,
                StripeCustomerId = request.StripeCustomerId,
                StripeSubscriptionId = request.StripeSubscriptionId,
                TrialEndsAt = request.SubscriptionStatus == "Trial" ? now.AddDays(14) : null,
                CreatedAt = now,
                UpdatedAt = now,
                AdminEmails = adminEmails,
                Notes = request.Notes
            };

            _logger.LogInformation("Creating tenant {TenantId} for organization {OrgName} (Azure: {AzureTenantId})",
                tenantId, request.OrganizationName, request.AzureTenantId);

            await _tenantsCollection.InsertOneAsync(tenant);

            _logger.LogInformation("Successfully created tenant {TenantId} in MongoDB", tenantId);
            return tenantId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create tenant for organization {OrgName}", request.OrganizationName);
            throw;
        }
    }

    public async Task UpdateTenantAsync(string azureTenantId, UpdateTenantRequest request)
    {
        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var updates = new List<UpdateDefinition<TenantSubscription>>
        {
            Builders<TenantSubscription>.Update.Set(t => t.UpdatedAt, DateTime.UtcNow)
        };

        if (request.OrganizationName != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.OrganizationName, request.OrganizationName));
        if (request.Tier != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.Tier, request.Tier));
        if (request.SubscriptionStatus != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.SubscriptionStatus, request.SubscriptionStatus));
        if (request.AdminEmails != null)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.AdminEmails, request.AdminEmails));
        if (request.IsDemo.HasValue)
            updates.Add(Builders<TenantSubscription>.Update.Set(t => t.IsDemo, request.IsDemo.Value));
        updates.Add(Builders<TenantSubscription>.Update.Set(t => t.Notes, request.Notes));

        var update = Builders<TenantSubscription>.Update.Combine(updates);
        var result = await _tenantsCollection.UpdateOneAsync(filter, update);
        if (result.MatchedCount == 0)
            throw new KeyNotFoundException($"Tenant with AzureTenantId '{azureTenantId}' not found.");
        _logger.LogInformation("Updated tenant {AzureTenantId}: {OrgName}", azureTenantId, request.OrganizationName);
    }

    public async Task DeleteTenantAsync(string azureTenantId)
    {
        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var result = await _tenantsCollection.DeleteOneAsync(filter);
        if (result.DeletedCount == 0)
            throw new KeyNotFoundException($"Tenant with AzureTenantId '{azureTenantId}' not found.");
        _logger.LogInformation("Deleted tenant {AzureTenantId}", azureTenantId);
    }

    public async Task<List<TenantSubscription>> GetAllSubscriptionsAsync()
    {
        var tenants = await _tenantsCollection
            .Find(Builders<TenantSubscription>.Filter.Empty)
            .SortByDescending(t => t.CreatedAt)
            .ToListAsync();
        return tenants;
    }

    public async Task UpdateSubscriptionStatusAsync(string azureTenantId, string status)
    {
        var filter = Builders<TenantSubscription>.Filter.Eq(t => t.AzureTenantId, azureTenantId);
        var update = Builders<TenantSubscription>.Update
            .Set(t => t.SubscriptionStatus, status)
            .Set(t => t.UpdatedAt, DateTime.UtcNow);
        await _tenantsCollection.UpdateOneAsync(filter, update);
        _logger.LogInformation("Updated subscription status for tenant {TenantId} to {Status}", azureTenantId, status);
    }

    public async Task<List<TenantSubscription>> GetTenantsForUserAsync(string userEmail)
    {
        if (string.IsNullOrEmpty(userEmail))
            return new List<TenantSubscription>();

        try
        {
            _logger.LogInformation("Finding tenants for user {Email}", userEmail);
            var emailLower = userEmail.ToLowerInvariant();

            // Find all tenants where the user's email appears in adminEmails
            var adminFilter = Builders<TenantSubscription>.Filter.AnyIn(
                t => t.AdminEmails, new[] { userEmail, emailLower });
            var tenantsByAdmin = await _tenantsCollection.Find(adminFilter).ToListAsync();

            // Also check TenantUsers collection for tenants the user has a role in
            var tenantUserFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("emailNormalized", emailLower),
                Builders<BsonDocument>.Filter.Eq("status", "Active"));
            var tenantUsers = await _tenantUsersCollection.Find(tenantUserFilter).ToListAsync();

            var additionalTenantIds = tenantUsers
                .Select(tu => tu.GetValue("tenantId", "").AsString)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet();

            // Fetch any additional tenants found via TenantUsers that weren't in admin results
            var existingTenantIds = tenantsByAdmin.Select(t => t.TenantId).ToHashSet();
            var missingTenantIds = additionalTenantIds.Except(existingTenantIds).ToList();

            if (missingTenantIds.Count > 0)
            {
                var tenantIdFilter = Builders<TenantSubscription>.Filter.In(t => t.TenantId, missingTenantIds);
                var additionalTenants = await _tenantsCollection.Find(tenantIdFilter).ToListAsync();
                tenantsByAdmin.AddRange(additionalTenants);
            }

            _logger.LogInformation("Found {Count} tenants for user {Email}", tenantsByAdmin.Count, userEmail);
            return tenantsByAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding tenants for user {Email}", userEmail);
            return new List<TenantSubscription>();
        }
    }
}

public class SalesInquiryService : ISalesInquiryService
{
    private readonly IMongoCollection<SalesInquiry> _inquiriesCollection;
    private readonly IEmailNotificationService _emailNotificationService;
    private readonly ILogger<SalesInquiryService> _logger;

    public SalesInquiryService(IMongoClient mongoClient, IConfiguration configuration,
        IEmailNotificationService emailNotificationService, ILogger<SalesInquiryService> logger)
    {
        _logger = logger;
        _emailNotificationService = emailNotificationService;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _inquiriesCollection = db.GetCollection<SalesInquiry>(
            configuration["MongoDB:SalesInquiriesCollection"] ?? "SalesInquiries");
    }

    public async Task<string> CreateInquiryAsync(CreateSalesInquiryRequest request)
    {
        try
        {
            var inquiryId = $"inquiry-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;

            var inquiry = new SalesInquiry
            {
                Id = inquiryId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Phone = request.Phone,
                CompanyName = request.CompanyName,
                JobTitle = request.JobTitle,
                InquiryType = request.InquiryType,
                Message = request.Message,
                Status = "New",
                Source = request.Source,
                CreatedAt = now,
                ContactedAt = null,
                Notes = null
            };

            _logger.LogInformation("Creating sales inquiry {InquiryId} from {Email} at {Company}",
                inquiryId, request.Email, request.CompanyName);

            await _inquiriesCollection.InsertOneAsync(inquiry);

            _logger.LogInformation("Successfully created sales inquiry {InquiryId}", inquiryId);

            await _emailNotificationService.SendSalesInquiryNotificationAsync(inquiry);

            return inquiryId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sales inquiry from {Email}", request.Email);
            throw;
        }
    }

    public async Task<List<SalesInquiry>> GetInquiriesAsync(string? status = null, int limit = 100)
    {
        try
        {
            FilterDefinition<SalesInquiry> filter = status == null
                ? Builders<SalesInquiry>.Filter.Empty
                : Builders<SalesInquiry>.Filter.Eq(i => i.Status, status);

            var results = await _inquiriesCollection
                .Find(filter)
                .SortByDescending(i => i.CreatedAt)
                .Limit(limit)
                .ToListAsync();

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales inquiries");
            return new List<SalesInquiry>();
        }
    }

    public async Task<SalesInquiry?> GetInquiryByIdAsync(string inquiryId)
    {
        try
        {
            var filter = Builders<SalesInquiry>.Filter.Eq(i => i.Id, inquiryId);
            return await _inquiriesCollection.Find(filter).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales inquiry {InquiryId}", inquiryId);
            return null;
        }
    }

    public async Task UpdateInquiryStatusAsync(string inquiryId, string status, string? notes = null)
    {
        try
        {
            var filter = Builders<SalesInquiry>.Filter.Eq(i => i.Id, inquiryId);
            var inquiry = await _inquiriesCollection.Find(filter).FirstOrDefaultAsync();

            if (inquiry == null)
                throw new InvalidOperationException($"Inquiry {inquiryId} not found");

            var updates = new List<UpdateDefinition<SalesInquiry>>
            {
                Builders<SalesInquiry>.Update.Set(i => i.Status, status)
            };

            if (notes != null)
                updates.Add(Builders<SalesInquiry>.Update.Set(i => i.Notes, notes));

            if (status == "Contacted" && inquiry.ContactedAt == null)
                updates.Add(Builders<SalesInquiry>.Update.Set(i => i.ContactedAt, DateTime.UtcNow));

            var combinedUpdate = Builders<SalesInquiry>.Update.Combine(updates);
            await _inquiriesCollection.UpdateOneAsync(filter, combinedUpdate);

            _logger.LogInformation("Updated sales inquiry {InquiryId} status to {Status}", inquiryId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating sales inquiry {InquiryId}", inquiryId);
            throw;
        }
    }
}

public class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailNotificationService> _logger;

    public SmtpEmailNotificationService(IConfiguration configuration, ILogger<SmtpEmailNotificationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendSalesInquiryNotificationAsync(SalesInquiry inquiry)
    {
        var smtpHost = _configuration["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogWarning("SMTP host is not configured. Skipping email notification for inquiry {InquiryId}", inquiry.Id);
            return;
        }

        if (string.IsNullOrWhiteSpace(inquiry.Email) || !IsValidEmail(inquiry.Email))
        {
            _logger.LogWarning("Invalid submitter email for inquiry {InquiryId}. Skipping confirmation email.", inquiry.Id);
        }

        var smtpPort = int.TryParse(_configuration["Email:SmtpPort"], out var port) ? port : 587;
        var enableSsl = !string.Equals(_configuration["Email:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);
        var fromAddress = _configuration["Email:FromAddress"] ?? "noreply@cloudhealthoffice.com";
        var salesTeamAddress = _configuration["Email:SalesTeamAddress"] ?? "sales@cloudhealthoffice.com";
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = enableSsl,
            Credentials = !string.IsNullOrWhiteSpace(username)
                ? new NetworkCredential(username, password)
                : CredentialCache.DefaultNetworkCredentials
        };

        try
        {
            using var salesNotification = BuildSalesTeamEmail(fromAddress, salesTeamAddress, inquiry);
            await client.SendMailAsync(salesNotification);
            _logger.LogInformation("Sales team notification sent for inquiry {InquiryId}", inquiry.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send sales team notification for inquiry {InquiryId}", inquiry.Id);
        }

        if (!string.IsNullOrWhiteSpace(inquiry.Email) && IsValidEmail(inquiry.Email))
        {
            try
            {
                using var confirmation = BuildConfirmationEmail(fromAddress, inquiry);
                await client.SendMailAsync(confirmation);
                _logger.LogInformation("Confirmation email sent to {Email} for inquiry {InquiryId}", inquiry.Email, inquiry.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email to {Email} for inquiry {InquiryId}", inquiry.Email, inquiry.Id);
            }
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static MailMessage BuildSalesTeamEmail(string from, string to, SalesInquiry inquiry)
    {
        var body =
            $"New Sales Inquiry Received\n\n" +
            $"Inquiry ID:  {inquiry.Id}\n" +
            $"Submitted:   {inquiry.CreatedAt:yyyy-MM-dd HH:mm} UTC\n\n" +
            $"Contact Information\n" +
            $"-------------------\n" +
            $"Name:        {inquiry.FirstName} {inquiry.LastName}\n" +
            $"Email:       {inquiry.Email}\n" +
            $"Phone:       {inquiry.Phone ?? "Not provided"}\n" +
            $"Company:     {inquiry.CompanyName}\n" +
            $"Job Title:   {inquiry.JobTitle ?? "Not provided"}\n\n" +
            $"Inquiry Details\n" +
            $"---------------\n" +
            $"Type:        {inquiry.InquiryType}\n" +
            $"Message:\n{inquiry.Message}\n\n" +
            $"Source: {inquiry.Source}\n\n" +
            $"Reply directly to this email to reach the prospect.";

        var message = new MailMessage(from, to)
        {
            Subject = $"[Cloud Health Office] New Sales Inquiry from {inquiry.CompanyName} – {inquiry.InquiryType}",
            Body = body,
            IsBodyHtml = false
        };
        message.ReplyToList.Add(new MailAddress(inquiry.Email, $"{inquiry.FirstName} {inquiry.LastName}"));
        return message;
    }

    private static MailMessage BuildConfirmationEmail(string from, SalesInquiry inquiry)
    {
        var body =
            $"Hi {inquiry.FirstName},\n\n" +
            $"Thank you for reaching out to Cloud Health Office!\n\n" +
            $"We have received your inquiry and our sales team will be in touch within 1 business day.\n\n" +
            $"Your reference ID is: {inquiry.Id}\n\n" +
            $"Inquiry Summary\n" +
            $"---------------\n" +
            $"Type:    {inquiry.InquiryType}\n" +
            $"Company: {inquiry.CompanyName}\n\n" +
            $"If you have urgent questions in the meantime, please email us at sales@cloudhealthoffice.com.\n\n" +
            $"Best regards,\n" +
            $"The Cloud Health Office Sales Team";

        return new MailMessage(from, inquiry.Email)
        {
            Subject = $"[Cloud Health Office] We received your inquiry – {inquiry.Id}",
            Body = body,
            IsBodyHtml = false
        };
    }
}

// OperatingModeService intentionally returns defaults when tenant-service is unreachable.
// Missing config should default to Replace mode, not error.
public class OperatingModeService : IOperatingModeService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OperatingModeService> _logger;

    public OperatingModeService(HttpClient httpClient, IConfiguration configuration, ILogger<OperatingModeService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId)
    {
        var baseUrl = _configuration["Services:TenantService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<OperatingModeConfiguration>(
                $"{baseUrl}/v1/tenants/{tenantId}/operating-mode");
            if (result != null)
                return NormalizeConfiguration(result, tenantId);

            return GetDefaultConfiguration(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch operating mode for tenant {TenantId}, returning defaults", tenantId);
            return GetDefaultConfiguration(tenantId);
        }
    }

    private static OperatingModeConfiguration NormalizeConfiguration(OperatingModeConfiguration config, string tenantId)
    {
        var merged = new Dictionary<string, string>(OperatingModeConfiguration.DefaultEngines, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in config.Engines)
        {
            merged[kvp.Key] = kvp.Value;
        }

        config.TenantId = string.IsNullOrEmpty(config.TenantId) ? tenantId : config.TenantId;
        config.Engines = merged;
        return config;
    }

    private static OperatingModeConfiguration GetDefaultConfiguration(string tenantId)
    {
        return new OperatingModeConfiguration
        {
            TenantId = tenantId,
            Engines = new Dictionary<string, string>(OperatingModeConfiguration.DefaultEngines, StringComparer.OrdinalIgnoreCase),
            UpdatedAt = null
        };
    }
}

// ── EDI Operations Service ─────────────────────────────────────────────

public class EdiOperationsService : IEdiOperationsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EdiOperationsService> _logger;

    public EdiOperationsService(HttpClient httpClient, IConfiguration configuration, ILogger<EdiOperationsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Edi834Batch>> Get834BatchesAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/edi/834-batches" +
                (from.HasValue ? $"?from={from:yyyy-MM-dd}" : "") +
                (to.HasValue ? (from.HasValue ? $"&to={to:yyyy-MM-dd}" : $"?to={to:yyyy-MM-dd}") : "");
            var result = await _httpClient.GetFromJsonAsync<List<Edi834Batch>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<Enrollment834Record>> Get834BatchRecordsAsync(string batchId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<Enrollment834Record>>($"{baseUrl}/edi/834-batches/{batchId}/records");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task Resolve834RecordAsync(Edi834ResolutionRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/edi/834-batches/{request.BatchId}/resolve", request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<ClaimAcknowledgmentSummary>> Get277CaAcknowledgmentsAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<ClaimAcknowledgmentSummary>>($"{baseUrl}/edi/277ca");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<Stream> Download277CaAsync(string claimId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/claims/{claimId}/277ca");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }

    public async Task<List<EraSummary>> GetErasAsync(DateTime? from = null, DateTime? to = null)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<EraSummary>>($"{baseUrl}/payments/eras");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<Stream> DownloadEraAsync(string paymentId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/payments/{paymentId}/835");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<List<EdiTransactionHistoryItem>> GetTransactionHistoryAsync(DateTime? from, DateTime? to, string? transactionType, string? partnerId, string? status, int pageNumber, int pageSize)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/edi/history?page={pageNumber}&pageSize={pageSize}" +
                (transactionType != null ? $"&type={transactionType}" : "") +
                (status != null ? $"&status={status}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<EdiTransactionHistoryItem>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "EDI Operations");
            throw new ServiceUnavailableException("EDI Operations", ex);
        }
    }
}

// ── Payment Run Service ─────────────────────────────────────────────────

public class PaymentRunService : IPaymentRunService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentRunService> _logger;

    public PaymentRunService(HttpClient httpClient, IConfiguration configuration, ILogger<PaymentRunService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<PaymentRunSummary>> GetPaymentRunsAsync(int limit = 50)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PaymentRunSummary>>($"{baseUrl}/paymentruns?limit={limit}");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<PaymentRunDetails?> GetPaymentRunByIdAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<PaymentRunDetails>($"{baseUrl}/paymentruns/{runId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<string> CreatePaymentRunAsync(CreatePaymentRunRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/paymentruns", request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CreateRunResponse>();
            return result?.RunId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task CancelPaymentRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var response = await _httpClient.PostAsync($"{baseUrl}/paymentruns/{runId}/cancel", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<Stream> DownloadEraForRunAsync(string runId)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/paymentruns/{runId}/835");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    private class CreateRunResponse { public string RunId { get; set; } = string.Empty; }
}

// ── Premium Billing Service ────────────────────────────────────────────

public class PremiumBillingService : IPremiumBillingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PremiumBillingService> _logger;

    public PremiumBillingService(HttpClient httpClient, IConfiguration configuration, ILogger<PremiumBillingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<BillingCycle>> GetBillingCyclesAsync(string? sponsorId = null, string? status = null)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var url = $"{baseUrl}/v1/billing-runs" + (sponsorId != null ? $"?sponsorId={sponsorId}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<BillingCycle>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<BillingCycleDetails?> GetBillingCycleByIdAsync(string cycleId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<BillingCycleDetails>($"{baseUrl}/v1/billing-runs/{cycleId}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<string> GenerateInvoiceAsync(CreateInvoiceRequest request)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/v1/billing-runs", request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<CreateCycleResponse>())?.CycleId ?? string.Empty;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<List<PremiumRate>> GetPremiumRatesAsync(string? planId = null)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var url = $"{baseUrl}/v1/premium-invoices" + (planId != null ? $"?planId={planId}" : "");
            var result = await _httpClient.GetFromJsonAsync<List<PremiumRate>>(url);
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task UpdatePremiumRateAsync(string rateId, decimal newRate, DateTime effectiveDate)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{baseUrl}/v1/premium-invoices/{rateId}", new { Rate = newRate, EffectiveDate = effectiveDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task MarkCycleAsPaidAsync(string cycleId, DateTime paidDate)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/v1/billing-runs/{cycleId}/mark-paid", new { PaidDate = paidDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<Stream> DownloadInvoiceAsync(string cycleId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            return await _httpClient.GetStreamAsync($"{baseUrl}/v1/billing-runs/{cycleId}/invoice");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    public async Task<MemberPremiumSummary?> GetMemberPremiumSummaryAsync(string memberId)
    {
        var baseUrl = _configuration["Services:BillingService"];
        try
        {
            var response = await _httpClient.GetAsync($"{baseUrl}/v1/members/{Uri.EscapeDataString(memberId)}/premium-summary");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberPremiumSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Billing Service");
            throw new ServiceUnavailableException("Billing Service", ex);
        }
    }

    private class CreateCycleResponse { public string CycleId { get; set; } = string.Empty; }
}

// ── Reporting Service ───────────────────────────────────────────────────

public class ReportingService : IReportingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportingService> _logger;

    public ReportingService(HttpClient httpClient, IConfiguration configuration, ILogger<ReportingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ClaimsSummaryReport> GetClaimsSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/claims-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<ClaimsSummaryReport>()
                ?? throw new Exception("Empty response from claims summary report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<PaymentSummaryReport> GetPaymentSummaryAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:PaymentService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/payment-summary", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<PaymentSummaryReport>()
                ?? throw new Exception("Empty response from payment summary report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Payment Service");
            throw new ServiceUnavailableException("Payment Service", ex);
        }
    }

    public async Task<EligibilityStatsReport> GetEligibilityStatsAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:EligibilityService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/eligibility-stats", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<EligibilityStatsReport>()
                ?? throw new Exception("Empty response from eligibility stats report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Eligibility Service");
            throw new ServiceUnavailableException("Eligibility Service", ex);
        }
    }

    public async Task<AuthApprovalReport> GetAuthApprovalReportAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:AuthorizationService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/auth-approval", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<AuthApprovalReport>()
                ?? throw new Exception("Empty response from auth approval report");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Authorization Service");
            throw new ServiceUnavailableException("Authorization Service", ex);
        }
    }

    public async Task<List<ClaimsByProvider>> GetProviderPerformanceAsync(ReportRequest request)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var result = await _httpClient.PostAsJsonAsync($"{baseUrl}/reports/provider-performance", request);
            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<List<ClaimsByProvider>>() ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Work Queue Service
// ---------------------------------------------------------------------------

public class WorkQueueService : IWorkQueueService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkQueueService> _logger;

    public WorkQueueService(HttpClient httpClient, IConfiguration configuration, ILogger<WorkQueueService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WorkQueueSummary> GetQueueSummaryAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<WorkQueueSummary>($"{baseUrl}/Claims/work-queue/summary");
            return summary ?? new WorkQueueSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<WorkQueueItem>> GetQueueItemsAsync(string? queueType = null,
        string? assignedTo = null, int limit = 100)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/Claims/work-queue/items?limit={limit}";
            if (!string.IsNullOrEmpty(queueType)) url += $"&queueType={Uri.EscapeDataString(queueType)}";
            if (!string.IsNullOrEmpty(assignedTo)) url += $"&assignedTo={Uri.EscapeDataString(assignedTo)}";
            var items = await _httpClient.GetFromJsonAsync<List<WorkQueueItem>>(url);
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task AssignClaimAsync(string claimId, string assignTo)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/Claims/work-queue/{Uri.EscapeDataString(claimId)}/assign",
                new { AssignTo = assignTo });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task OverrideAsync(string claimId, string overrideReason)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/Claims/work-queue/{Uri.EscapeDataString(claimId)}/override",
                new { OverrideReason = overrideReason });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task ResolvePendedClaimAsync(
        string claimId,
        string disposition,
        string reason,
        string? aiExaminerAgreement,
        string examinerUserId)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{baseUrl}/Claims/work-queue/{Uri.EscapeDataString(claimId)}/resolve",
                new
                {
                    disposition,
                    reason,
                    aiExaminerAgreement,
                    examinerUserId,
                });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Enrollment Operations Service
// ---------------------------------------------------------------------------

public class EnrollmentOperationsService : IEnrollmentOperationsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EnrollmentOperationsService> _logger;

    public EnrollmentOperationsService(HttpClient httpClient, IConfiguration configuration, ILogger<EnrollmentOperationsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EnrollmentDailySummary> GetTodaySummaryAsync()
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<EnrollmentDailySummary>($"{baseUrl}/enrollment-ops/summary/today");
            return summary ?? new EnrollmentDailySummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<List<EnrollmentFile>> GetRecentFilesAsync(int days = 7)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var files = await _httpClient.GetFromJsonAsync<List<EnrollmentFile>>($"{baseUrl}/enrollment-ops/files?days={days}");
            return files ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<EnrollmentFileDetail> GetFileDetailAsync(string fileId)
    {
        var baseUrl = _configuration["Services:MemberService"];
        try
        {
            var detail = await _httpClient.GetFromJsonAsync<EnrollmentFileDetail>($"{baseUrl}/enrollment-ops/files/{Uri.EscapeDataString(fileId)}");
            return detail ?? throw new Exception($"File {fileId} not found");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Appeals Service
// ---------------------------------------------------------------------------

public class AppealsService : IAppealsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppealsService> _logger;

    public AppealsService(HttpClient httpClient, IConfiguration configuration, ILogger<AppealsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AppealsSummary> GetSummaryAsync()
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<AppealsSummary>($"{baseUrl}/appeals/summary");
            return summary ?? new AppealsSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    public async Task<List<AppealSummary>> SearchAppealsAsync(string? appealId = null,
        string? memberId = null, string? originalClaimId = null)
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            var queryParts = new List<string>();
            if (!string.IsNullOrEmpty(appealId)) queryParts.Add($"appealId={Uri.EscapeDataString(appealId)}");
            if (!string.IsNullOrEmpty(memberId)) queryParts.Add($"memberId={Uri.EscapeDataString(memberId)}");
            if (!string.IsNullOrEmpty(originalClaimId)) queryParts.Add($"originalClaimId={Uri.EscapeDataString(originalClaimId)}");
            var query = string.Join("&", queryParts);
            var results = await _httpClient.GetFromJsonAsync<List<AppealSummary>>($"{baseUrl}/appeals/search?{query}");
            return results ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }

    public async Task<AppealDetails?> GetAppealByIdAsync(string appealId)
    {
        var baseUrl = _configuration["Services:AppealsService"];
        try
        {
            return await _httpClient.GetFromJsonAsync<AppealDetails>($"{baseUrl}/appeals/{Uri.EscapeDataString(appealId)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Appeals Service");
            throw new ServiceUnavailableException("Appeals Service", ex);
        }
    }
}

// ---------------------------------------------------------------------------
// Correspondence Service
// ---------------------------------------------------------------------------

public class CorrespondenceService : ICorrespondenceService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CorrespondenceService> _logger;

    public CorrespondenceService(HttpClient httpClient, IConfiguration configuration, ILogger<CorrespondenceService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CorrespondenceSummary> GetSummaryAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<CorrespondenceSummary>($"{baseUrl}/correspondence/summary");
            return summary ?? new CorrespondenceSummary();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<CorrespondenceItem>> GetQueueAsync(string? type = null,
        string? status = null, int limit = 50)
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var url = $"{baseUrl}/correspondence/queue?limit={limit}";
            if (!string.IsNullOrEmpty(type)) url += $"&type={Uri.EscapeDataString(type)}";
            if (!string.IsNullOrEmpty(status)) url += $"&status={Uri.EscapeDataString(status)}";
            var items = await _httpClient.GetFromJsonAsync<List<CorrespondenceItem>>(url);
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }

    public async Task<List<RfaiTrackingItem>> GetOutstandingRfaisAsync()
    {
        var baseUrl = _configuration["Services:ClaimsService"];
        try
        {
            var items = await _httpClient.GetFromJsonAsync<List<RfaiTrackingItem>>($"{baseUrl}/correspondence/rfais/outstanding");
            return items ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Claims Service");
            throw new ServiceUnavailableException("Claims Service", ex);
        }
    }
}

public class PricingApiService : IPricingApiService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PricingApiService> _logger;

    public PricingApiService(HttpClient httpClient, IConfiguration configuration, ILogger<PricingApiService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:PricingApi"] ?? "http://pricing-api.cloudhealthoffice";
    private string AdminSecret => _configuration["PricingApi:AdminSecret"] ?? "";

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Admin-Secret", AdminSecret);
        return request;
    }

    public async Task<List<PricingApiKey>> GetApiKeysAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Get, $"{BaseUrl}/api/v1/admin/api-keys");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var keys = await response.Content.ReadFromJsonAsync<List<PricingApiKey>>();
            return keys ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<PricingApiKey> CreateApiKeyAsync(string tenantName, string contactEmail, string tier)
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/api-keys");
            request.Content = JsonContent.Create(new { tenantName, contactEmail, tier });
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var key = await response.Content.ReadFromJsonAsync<PricingApiKey>();
            return key ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task DeactivateApiKeyAsync(string apiKey)
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Delete, $"{BaseUrl}/api/v1/admin/api-keys/{Uri.EscapeDataString(apiKey)}");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task ResetUsageAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/api-keys/reset-usage");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<List<PricingFeeScheduleInfo>> GetFeeSchedulesAsync()
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<List<PricingFeeScheduleInfo>>($"{BaseUrl}/api/v1/fee-schedules");
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task<FeeScheduleUploadResult> UploadFeeScheduleAsync(string type, int year, Stream csvStream, string fileName, decimal? baseRate = null)
    {
        try
        {
            var url = $"{BaseUrl}/api/v1/admin/fee-schedules/upload/{type.ToLowerInvariant()}?year={year}";
            if (baseRate.HasValue)
                url += $"&baseRate={baseRate.Value}";

            var request = CreateAdminRequest(HttpMethod.Post, url);
            var content = new MultipartFormDataContent();
            content.Add(new StreamContent(csvStream), "file", fileName);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<FeeScheduleUploadResult>();
            return result ?? new();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }

    public async Task SeedDemoDataAsync()
    {
        try
        {
            var request = CreateAdminRequest(HttpMethod.Post, $"{BaseUrl}/api/v1/admin/fee-schedules/seed-demo");
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Pricing API");
            throw new ServiceUnavailableException("Pricing API", ex);
        }
    }
}

// ── Capitation Service ────────────────────────────────────────────────────

public class CapitationService : ICapitationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CapitationService> _logger;

    public CapitationService(HttpClient httpClient, IConfiguration configuration, ILogger<CapitationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:CapitationService"] ?? "http://capitation-service.cloudhealthoffice/api";

    // Contracts
    public async Task<List<CapitationContractSummary>> GetContractsAsync(string? npi = null, string? status = null, string? lob = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapitationContractSummary>>($"{BaseUrl}/v1/capitation/contracts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapitationContractSummary?> GetContractByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapitationContractSummary>($"{BaseUrl}/v1/capitation/contracts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<string> CreateContractAsync(CapitationContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/contracts", contract);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<CapitationContractSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task UpdateContractAsync(string id, CapitationContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/contracts/{id}", contract);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task ActivateContractAsync(string id)
    {
        try
        {
            var response = await _httpClient.PutAsync($"{BaseUrl}/v1/capitation/contracts/{id}/activate", null);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/contracts/{id}/terminate",
                new { Reason = reason, TerminationDate = terminationDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Runs
    public async Task<List<CapRunSummary>> GetRunsAsync(DateTime? from = null, DateTime? to = null, string? lineOfBusiness = null)
    {
        try
        {
            var qs = new List<string>();
            if (from.HasValue) qs.Add($"from={from.Value:O}");
            if (to.HasValue) qs.Add($"to={to.Value:O}");
            if (!string.IsNullOrEmpty(lineOfBusiness)) qs.Add($"lineOfBusiness={lineOfBusiness}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapRunSummary>>($"{BaseUrl}/v1/capitation/runs{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapRunSummary?> GetRunByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapRunSummary>($"{BaseUrl}/v1/capitation/runs/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<string> CreateRunAsync(CreateCapRunRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/runs", request);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<CapRunSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapRunSummary> ExecuteRunAsync(string id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"{BaseUrl}/v1/capitation/runs/{id}/execute", null);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CapRunSummary>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task CancelRunAsync(string id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"{BaseUrl}/v1/capitation/runs/{id}");
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Statements
    public async Task<List<CapStatementSummary>> GetStatementsAsync(string? npi = null, DateTime? periodFrom = null, DateTime? periodTo = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (periodFrom.HasValue) qs.Add($"periodFrom={periodFrom.Value:O}");
            if (periodTo.HasValue) qs.Add($"periodTo={periodTo.Value:O}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/statements{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapStatementSummary?> GetStatementByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CapStatementSummary>($"{BaseUrl}/v1/capitation/statements/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<CapStatementSummary>> GetStatementsByRunAsync(string runId)
    {
        try { return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/runs/{runId}/statements") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<List<CapStatementSummary>> GetUnpaidStatementsAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<List<CapStatementSummary>>($"{BaseUrl}/v1/capitation/statements/unpaid") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task ApproveStatementAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/capitation/statements/{id}/approve", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task VoidStatementAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/statements/{id}/void", new { Reason = reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task HoldStatementAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/capitation/statements/{id}/hold", new { Reason = reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapitationPeriodSummaryDto> GetPeriodSummaryAsync(DateTime period)
    {
        try { return await _httpClient.GetFromJsonAsync<CapitationPeriodSummaryDto>($"{BaseUrl}/v1/capitation/statements/summary?period={period:O}") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    // Disbursements
    public async Task<string> InitiateDisbursementAsync(string statementId, string? initiatedBy = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/disbursements",
                new { StatementId = statementId, InitiatedBy = initiatedBy });
            response.EnsureSuccessStatusCode();
            return "ok";
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }

    public async Task<CapDisbursementBatchResult> InitiateBatchDisbursementAsync(List<string> statementIds, string? initiatedBy = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/capitation/disbursements/batch",
                new { StatementIds = statementIds, InitiatedBy = initiatedBy });
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CapDisbursementBatchResult>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Capitation Service unavailable"); throw new ServiceUnavailableException("Capitation Service", ex); }
    }
}

// ── Provider Contracts Service ──────────────────────────────────────────────

public class ProviderContractsService : IProviderContractsService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderContractsService> _logger;

    public ProviderContractsService(HttpClient httpClient, IConfiguration configuration, ILogger<ProviderContractsService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:ProviderContractsService"] ?? "http://provider-contracts-service.cloudhealthoffice/api";

    public async Task<List<ProviderContractSummary>> GetContractsAsync(
        string? npi = null, string? lob = null,
        string? status = null, string? paymentMethodology = null,
        string? networkStatus = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(npi)) qs.Add($"npi={npi}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (!string.IsNullOrEmpty(paymentMethodology)) qs.Add($"paymentMethodology={paymentMethodology}");
            if (!string.IsNullOrEmpty(networkStatus)) qs.Add($"networkStatus={networkStatus}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ProviderContractSummary>>($"{BaseUrl}/v1/contracts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<ProviderContractSummary?> GetContractByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ProviderContractSummary>($"{BaseUrl}/v1/contracts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<ProviderContractSummary?> GetContractByNumberAsync(string number)
    {
        try { return await _httpClient.GetFromJsonAsync<ProviderContractSummary>($"{BaseUrl}/v1/contracts/number/{number}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<string> CreateContractAsync(ProviderContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/contracts", contract);
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<ProviderContractSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task UpdateContractAsync(string id, ProviderContractSummary contract)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/contracts/{id}", contract);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task ActivateContractAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/contracts/{id}/activate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task SuspendContractAsync(string id, string reason)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/contracts/{id}/suspend", new { reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task TerminateContractAsync(string id, string reason, DateTime? terminationDate = null)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/contracts/{id}/terminate", new { reason, terminationDate }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task ReinstateContractAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/contracts/{id}/reinstate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task AddAmendmentAsync(string id, ContractAmendmentSummary amendment)
    {
        try { var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/contracts/{id}/amendments", amendment); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task SyncChildrenAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/contracts/{id}/sync-children", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    public async Task<List<string>> GetRateConfigIdsAsync(string id)
    {
        try
        {
            var result = await _httpClient.GetFromJsonAsync<RateConfigIdsResponse>($"{BaseUrl}/v1/contracts/{id}/rate-configs");
            var ids = new List<string>();
            if (result != null)
            {
                ids.AddRange(result.CapitationRateConfigIds ?? new());
                ids.AddRange(result.FfsRateConfigIds ?? new());
            }
            return ids;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "Provider Contracts Service unavailable"); throw new ServiceUnavailableException("Provider Contracts Service", ex); }
    }

    private class RateConfigIdsResponse
    {
        public List<string>? CapitationRateConfigIds { get; set; }
        public List<string>? FfsRateConfigIds { get; set; }
    }
}

// ── AR Service ──────────────────────────────────────────────────────────────

public class ArServiceImpl : IArService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArServiceImpl> _logger;

    public ArServiceImpl(HttpClient httpClient, IConfiguration configuration, ILogger<ArServiceImpl> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:ArService"] ?? "http://ar-service.cloudhealthoffice/api";

    // ── GL Accounts ─────────────────────────────────────────────────────
    public async Task<List<GlAccountSummary>> GetAccountsAsync(string? accountType = null, string? lob = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(accountType)) qs.Add($"accountType={accountType}");
            if (!string.IsNullOrEmpty(lob)) qs.Add($"lob={lob}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<GlAccountSummary>>($"{BaseUrl}/v1/ar/accounts{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<GlAccountSummary?> GetAccountByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<GlAccountSummary>($"{BaseUrl}/v1/ar/accounts/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateAccountAsync(GlAccountSummary account)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/accounts", account);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<GlAccountSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task UpdateAccountAsync(string id, GlAccountSummary account)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/ar/accounts/{id}", account); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ActivateAccountAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/ar/accounts/{id}/activate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task DeactivateAccountAsync(string id)
    {
        try { var r = await _httpClient.PutAsync($"{BaseUrl}/v1/ar/accounts/{id}/deactivate", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Balances ────────────────────────────────────────────────────────
    public async Task<List<ArBalanceSummary>> GetBalancesAsync(string? accountId = null, DateTime? period = null, bool? isReconciled = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(accountId)) qs.Add($"accountId={accountId}");
            if (period.HasValue) qs.Add($"period={period.Value:yyyy-MM-dd}");
            if (isReconciled.HasValue) qs.Add($"isReconciled={isReconciled.Value}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ArBalanceSummary>>($"{BaseUrl}/v1/ar/balances{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArBalanceSummary?> GetBalanceByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ArBalanceSummary>($"{BaseUrl}/v1/ar/balances/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<List<ArBalanceSummary>> GetBalancesByAccountAsync(string accountId)
    {
        try { return await _httpClient.GetFromJsonAsync<List<ArBalanceSummary>>($"{BaseUrl}/v1/ar/balances/account/{accountId}") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArAgingSummary> GetAgingSummaryAsync()
    {
        try { return await _httpClient.GetFromJsonAsync<ArAgingSummary>($"{BaseUrl}/v1/ar/balances/aging") ?? new(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ReconcileBalanceAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/balances/{id}/reconcile", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Cash Posting ────────────────────────────────────────────────────
    public async Task<List<CashPostingSummary>> GetCashPostingsAsync(string? payerType = null, string? status = null, DateTime? dateFrom = null, DateTime? dateTo = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(payerType)) qs.Add($"payerType={payerType}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (dateFrom.HasValue) qs.Add($"dateFrom={dateFrom.Value:yyyy-MM-dd}");
            if (dateTo.HasValue) qs.Add($"dateTo={dateTo.Value:yyyy-MM-dd}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<CashPostingSummary>>($"{BaseUrl}/v1/ar/cash-postings{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<CashPostingSummary?> GetCashPostingByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<CashPostingSummary>($"{BaseUrl}/v1/ar/cash-postings/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateCashPostingAsync(CashPostingSummary posting)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/cash-postings", posting);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<CashPostingSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ApplyCashPostingAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/cash-postings/{id}/apply", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task VoidCashPostingAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/cash-postings/{id}/void", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Adjustments ─────────────────────────────────────────────────────
    public async Task<List<ArAdjustmentSummary>> GetAdjustmentsAsync(string? type = null, string? status = null, DateTime? period = null, string? accountId = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(type)) qs.Add($"type={type}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            if (period.HasValue) qs.Add($"period={period.Value:yyyy-MM-dd}");
            if (!string.IsNullOrEmpty(accountId)) qs.Add($"glAccountId={accountId}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ArAdjustmentSummary>>($"{BaseUrl}/v1/ar/adjustments{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArAdjustmentSummary?> GetAdjustmentByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ArAdjustmentSummary>($"{BaseUrl}/v1/ar/adjustments/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateAdjustmentAsync(ArAdjustmentSummary adjustment)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/adjustments", adjustment);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<ArAdjustmentSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ApproveAdjustmentAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/adjustments/{id}/approve", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task RejectAdjustmentAsync(string id, string reason)
    {
        try { var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/adjustments/{id}/reject", new { reason }); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task PostAdjustmentAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/adjustments/{id}/post", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task ReverseAdjustmentAsync(string id)
    {
        try { var r = await _httpClient.PostAsync($"{BaseUrl}/v1/ar/adjustments/{id}/reverse", null); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    // ── Batch Rules ─────────────────────────────────────────────────────
    public async Task<List<ArBatchRuleSummary>> GetBatchRulesAsync(string? trigger = null, string? status = null)
    {
        try
        {
            var qs = new List<string>();
            if (!string.IsNullOrEmpty(trigger)) qs.Add($"trigger={trigger}");
            if (!string.IsNullOrEmpty(status)) qs.Add($"status={status}");
            var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
            return await _httpClient.GetFromJsonAsync<List<ArBatchRuleSummary>>($"{BaseUrl}/v1/ar/batch-rules{query}") ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArBatchRuleSummary?> GetBatchRuleByIdAsync(string id)
    {
        try { return await _httpClient.GetFromJsonAsync<ArBatchRuleSummary>($"{BaseUrl}/v1/ar/batch-rules/{id}"); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<string> CreateBatchRuleAsync(ArBatchRuleSummary rule)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/batch-rules", rule);
            r.EnsureSuccessStatusCode();
            var created = await r.Content.ReadFromJsonAsync<ArBatchRuleSummary>();
            return created?.Id ?? string.Empty;
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task UpdateBatchRuleAsync(string id, ArBatchRuleSummary rule)
    {
        try { var r = await _httpClient.PutAsJsonAsync($"{BaseUrl}/v1/ar/batch-rules/{id}", rule); r.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<ArBatchRuleTestResult> TestBatchRuleAsync(string id, decimal sampleAmount)
    {
        try
        {
            var r = await _httpClient.PostAsJsonAsync($"{BaseUrl}/v1/ar/batch-rules/{id}/test", new { sampleAmount });
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadFromJsonAsync<ArBatchRuleTestResult>() ?? new();
        }
        catch (HttpRequestException ex) { _logger.LogError(ex, "AR Service unavailable"); throw new ServiceUnavailableException("AR Service", ex); }
    }

    public async Task<MemberArSummary?> GetMemberArSummaryAsync(string memberId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{BaseUrl}/v1/members/{Uri.EscapeDataString(memberId)}/ar-summary");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MemberArSummary>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AR Service unavailable");
            throw new ServiceUnavailableException("AR Service", ex);
        }
    }
}

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

public class FamilyRelationshipService : IFamilyRelationshipService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FamilyRelationshipService> _logger;

    public FamilyRelationshipService(HttpClient httpClient, IConfiguration configuration, ILogger<FamilyRelationshipService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => _configuration["Services:MemberService"] ?? string.Empty;

    public async Task<List<FamilyRelationshipRow>> ListForMemberAsync(string memberId)
    {
        try
        {
            var payload = await _httpClient.GetFromJsonAsync<FamilyRelationshipListResponse>(
                $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships");
            return payload?.Relationships ?? new List<FamilyRelationshipRow>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<FamilyRelationshipRow?> AddDependentAsync(string subscriberMemberId, AddDependentPayload payload)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/members/{Uri.EscapeDataString(subscriberMemberId)}/dependents",
                payload);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Add dependent failed ({(int)response.StatusCode}): {body}");
            }

            // Server returns { member, subscriberMemberId, relationship }. Surface the
            // relationship row so callers can link into the new edge without a re-fetch.
            var parsed = await response.Content.ReadFromJsonAsync<AddDependentApiResponse>(
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.Relationship;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    private class AddDependentApiResponse
    {
        public FamilyRelationshipRow? Relationship { get; set; }
        public string SubscriberMemberId { get; set; } = string.Empty;
    }

    public async Task EndRelationshipAsync(string memberId, string relationshipId, DateTime? endDate = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships/{Uri.EscapeDataString(relationshipId)}/end",
                new { endDate });
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task<FamilyRelationshipRow?> UpdateRelationshipAsync(string memberId, string relationshipId, UpdateRelationshipPayload payload)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync(
                $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships/{Uri.EscapeDataString(relationshipId)}",
                payload);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<FamilyRelationshipRow>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    public async Task SoftDeleteAsync(string memberId, string relationshipId, string reason)
    {
        try
        {
            var url = $"{BaseUrl}/members/{Uri.EscapeDataString(memberId)}/relationships/{Uri.EscapeDataString(relationshipId)}?reason={Uri.EscapeDataString(reason)}";
            var response = await _httpClient.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "Member Service");
            throw new ServiceUnavailableException("Member Service", ex);
        }
    }

    private class FamilyRelationshipListResponse
    {
        public string MemberId { get; set; } = string.Empty;
        public List<FamilyRelationshipRow> Relationships { get; set; } = new();
        public int TotalCount { get; set; }
    }
}

public class IdCardService : IIdCardService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IdCardService> _logger;

    public IdCardService(HttpClient httpClient, IConfiguration configuration, ILogger<IdCardService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string IdCardBaseUrl =>
        _configuration["Services:IdCardService"] ?? "http://idcard-service.cloudhealthoffice/api/v1";

    private string MemberDocumentBaseUrl =>
        _configuration["Services:MemberDocumentService"] ?? "http://member-document-service.cloudhealthoffice";

    public async Task<IdCardOrderView> OrderAsync(string memberId, string? languageCode = null, string? requestedBy = null)
    {
        try
        {
            var body = new
            {
                memberId,
                channel = "Digital",
                languageCode,
                requestedBy
            };
            var response = await _httpClient.PostAsJsonAsync($"{IdCardBaseUrl}/id-cards/orders", body);
            response.EnsureSuccessStatusCode();
            var order = await response.Content.ReadFromJsonAsync<IdCardOrderView>();
            return order ?? throw new Exception("Empty order response");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }

    public async Task<IdCardOrderView?> GetOrderAsync(string orderId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<IdCardOrderView>($"{IdCardBaseUrl}/id-cards/{Uri.EscapeDataString(orderId)}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }

    public async Task<List<IdCardHistoryView>> ListForMemberAsync(string memberId)
    {
        try
        {
            var url = $"{IdCardBaseUrl}/members/{Uri.EscapeDataString(memberId)}/id-cards";
            var result = await _httpClient.GetFromJsonAsync<List<IdCardHistoryView>>(url);
            return result ?? new List<IdCardHistoryView>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }

    public string BuildDocumentDownloadUrl(string documentId) =>
        $"{MemberDocumentBaseUrl}/api/v1/member-documents/{Uri.EscapeDataString(documentId)}/content";

    public async Task RevokeAsync(string cardId, string reason, string? notes = null)
    {
        try
        {
            var body = new { reason, notes };
            var response = await _httpClient.PostAsJsonAsync(
                $"{IdCardBaseUrl}/id-cards/{Uri.EscapeDataString(cardId)}/revoke", body);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", "ID Card Service");
            throw new ServiceUnavailableException("ID Card Service", ex);
        }
    }
}
