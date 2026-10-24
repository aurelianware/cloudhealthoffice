using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;

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

    public async Task<CoverageResolution?> ResolveCoverageAsync(
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

            // Adjudication, including examiner approval re-runs, is a service
            // pipeline operation. Keep the caller tenant guard in the real
            // outbound handler; do not require downstream reads on user roles.
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
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
                    response.StatusCode, LogSanitizer.MaskId(memberId));
                return null;
            }

            var coverages = await response.Content
                .ReadFromJsonAsync<List<CoverageSummaryDto>>(JsonOptions, ct)
                .ConfigureAwait(false);

            // The first coverage carrying a plan id, as before -- the sponsor
            // hierarchy is read off that SAME coverage rather than from the
            // first entry that happens to have a group number, or a claim
            // could be priced on one coverage's plan and scoped by another's
            // sponsor.
            var coverage = coverages?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.PlanId));
            if (coverage?.PlanId is not { } planId) return null;

            return new CoverageResolution(
                planId,
                NullIfBlank(coverage.GroupNumber),
                NullIfBlank(coverage.SponsorPolicyNumber));
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(ex,
                "Active-coverage lookup failed for member {Member} tenant {Tenant}",
                LogSanitizer.MaskId(memberId), LogSanitizer.Sanitize(tenantId));
            return null;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class CoverageSummaryDto
    {
        [JsonPropertyName("planId")]
        public string? PlanId { get; set; }

        /// <summary>The sponsor's group number. Coverage marks it required, but
        /// this reads it as nullable: a downstream that stops sending it should
        /// narrow the setting scope, not throw on a claim.</summary>
        [JsonPropertyName("groupNumber")]
        public string? GroupNumber { get; set; }

        [JsonPropertyName("sponsorPolicyNumber")]
        public string? SponsorPolicyNumber { get; set; }
    }
}
