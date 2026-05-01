using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Services;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="ICredentialingStatusClient"/> calling
/// provider-service's
/// <c>GET /api/v1/providers/{id}/credentialing/status-as-of</c>
/// (capability 5.6). Sibling of <see cref="HttpProviderMembershipClient"/>
/// in shape: typed factory client, non-throwing, caching deferred to
/// the decorator.
/// </summary>
public class HttpCredentialingStatusClient : ICredentialingStatusClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpCredentialingStatusClient> _logger;

    public HttpCredentialingStatusClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpCredentialingStatusClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CredentialingStatusSnapshot?> GetStatusAsOfAsync(
        string tenantId,
        string providerId,
        DateTime asOfDate,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        _ = forceRefresh;

        if (string.IsNullOrWhiteSpace(providerId)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.ProviderService);
            var encodedId = Uri.EscapeDataString(providerId);
            var asOfQuery = asOfDate.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);

            var url = $"/api/v1/providers/{encodedId}/credentialing/status-as-of" +
                      $"?asOfDate={Uri.EscapeDataString(asOfQuery)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Provider not found in tenant — treat as Unknown rather
                // than degraded so the enforcement stage applies the
                // policy mode deterministically. Distinguishes
                // "definitively unknown" from "transport failed".
                return new CredentialingStatusSnapshot
                {
                    ProviderId = providerId,
                    AsOfDate = asOfDate,
                    Status = "Unknown",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Provider service returned {StatusCode} resolving credentialing status for provider {Provider}",
                    response.StatusCode, SanitizeForLog(providerId));
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<CredentialingStatusSnapshot>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(ex,
                "Credentialing status lookup failed for provider {Provider} tenant {Tenant}",
                SanitizeForLog(providerId), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
