using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Services;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="ICoverageClient"/> calling coverage-service's
/// <c>GET /api/v1/coverage/member/{memberId}/cob</c> (capability 5.8).
/// Sibling of <see cref="HttpCredentialingStatusClient"/> in shape:
/// typed factory client, non-throwing on transport failures, caching
/// deferred to the decorator.
///
/// <para>
/// <b>404 → empty list translation (Decision 14a — plan-phase ratified).</b>
/// coverage-service returns 404 when a member has zero COB entries on
/// record (equivalent to "CHO is the only coverage"). This client converts
/// that 404 into an empty <c>IReadOnlyList&lt;CobEntry&gt;</c> so the stage
/// can rely on the "empty list = CHO-only" semantic without disambiguating
/// status codes itself. Genuine transport failures (HttpRequestException,
/// timeout, JSON parse error) still surface as <c>null</c>.
/// </para>
/// </summary>
public class HttpCoverageClient : ICoverageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyList<CobEntry> Empty = Array.Empty<CobEntry>();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpCoverageClient> _logger;

    public HttpCoverageClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpCoverageClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CobEntry>?> GetCobEntriesAsync(
        string tenantId,
        string memberId,
        DateTime asOfDate,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        _ = forceRefresh;

        // Mirrors HttpCredentialingStatusClient / HttpProviderMembershipClient:
        // blank input → null (degraded), distinct from the canonical
        // "empty list = CHO is the only coverage" 404 path. The stage
        // already short-circuits with Reject before ever calling here
        // when MemberId is blank (Copilot review #737/5).
        if (string.IsNullOrWhiteSpace(memberId)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.CoverageService);
            var encodedId = Uri.EscapeDataString(memberId);
            var asOfQuery = asOfDate.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);

            var url = $"/api/v1/coverage/member/{encodedId}/cob" +
                      $"?asOfDate={Uri.EscapeDataString(asOfQuery)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Member has zero COB entries — CHO is the only coverage.
                // Distinct from transport failure: 404 is a definitive
                // "no other insurance" answer, not a degradation signal.
                return Empty;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Coverage service returned {StatusCode} resolving COB entries for member {Member}",
                    response.StatusCode, SanitizeForLog(memberId));
                return null;
            }

            var entries = await response.Content
                .ReadFromJsonAsync<List<CobEntry>>(JsonOptions, ct)
                .ConfigureAwait(false);

            return entries ?? (IReadOnlyList<CobEntry>)Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(ex,
                "COB lookup failed for member {Member} tenant {Tenant}",
                SanitizeForLog(memberId), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
