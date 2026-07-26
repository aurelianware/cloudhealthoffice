using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsService.Services;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="ICoverageResolver"/> calling coverage-service's
/// <c>GET /api/v1/coverage/member/{memberId}/active</c>. Reuses the same
/// named client as <see cref="HttpCoverageClient"/> (<see cref="UpstreamClientNames.CoverageService"/>)
/// — same base address, just a different endpoint on the same downstream.
/// </summary>
public class HttpCoverageResolver : ICoverageResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpCoverageResolver> _logger;

    public HttpCoverageResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpCoverageResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> ResolveBenefitPlanIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.CoverageService);
            var encodedId = Uri.EscapeDataString(memberId);
            var serviceDateQuery = serviceDate.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);

            var url = $"/api/v1/coverage/member/{encodedId}/active" +
                      $"?serviceDate={Uri.EscapeDataString(serviceDateQuery)}";
            if (!string.IsNullOrWhiteSpace(insuranceLineCode))
            {
                url += $"&insuranceLineCode={Uri.EscapeDataString(insuranceLineCode)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // No active coverage on record for this member/date/line —
                // a definitive answer, not a degradation signal.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Coverage service returned {StatusCode} resolving active coverage for member {Member}",
                    response.StatusCode, SanitizeForLog(memberId));
                return null;
            }

            var coverages = await response.Content
                .ReadFromJsonAsync<List<CoverageSummaryDto>>(JsonOptions, ct)
                .ConfigureAwait(false);

            return coverages?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.PlanId))?.PlanId;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(ex,
                "Active-coverage lookup failed for member {Member} tenant {Tenant}",
                SanitizeForLog(memberId), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private sealed class CoverageSummaryDto
    {
        [JsonPropertyName("planId")]
        public string? PlanId { get; set; }
    }
}
