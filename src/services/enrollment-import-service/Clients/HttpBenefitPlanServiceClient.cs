using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EnrollmentImportService.Clients;

public class HttpBenefitPlanServiceClient : IBenefitPlanServiceClient
{
    public const string HttpClientName = "BenefitPlanServiceRead";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpBenefitPlanServiceClient> _logger;

    public HttpBenefitPlanServiceClient(IHttpClientFactory httpClientFactory, ILogger<HttpBenefitPlanServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> ResolvePlanIdAsync(
        string tenantId, string groupNumber, string insuranceLineCode, string externalPlanCode,
        CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var query = $"groupNumber={Uri.EscapeDataString(groupNumber)}"
            + $"&insuranceLineCode={Uri.EscapeDataString(insuranceLineCode)}"
            + $"&externalPlanCode={Uri.EscapeDataString(externalPlanCode)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/plan-code-mappings/resolve?{query}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Only 404 means "no mapping". A refusal (401/403) or an outage is
            // not a plan-code gap, so it fails the lookup instead of being
            // counted as an unresolved code.
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning(
                "benefit-plan-service rejected plan-code resolve for group {GroupNumber} code {ExternalCode}: {Status} {Body}",
                SanitizeForLog(groupNumber), SanitizeForLog(externalPlanCode), response.StatusCode, SanitizeForLog(body));
            throw new HttpRequestException(
                $"benefit-plan-service plan-code resolve failed with {(int)response.StatusCode}.",
                null, response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<PlanCodeMappingResponseDto>(cancellationToken: ct)
            .ConfigureAwait(false);
        return result?.PlanId;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    /// <summary>Mirrors benefit-plan-service's PlanCodeMappingResponse — only PlanId is needed here.</summary>
    private sealed class PlanCodeMappingResponseDto
    {
        [JsonPropertyName("planId")]
        public string PlanId { get; set; } = string.Empty;
    }
}
