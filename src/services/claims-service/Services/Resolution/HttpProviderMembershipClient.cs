using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Services;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="IProviderMembershipClient"/> calling
/// provider-service's <c>GET /api/v1/networks/{id}/members/{npi}</c>
/// (capability 5.6). Mirrors <see cref="HttpBenefitPlanResolver"/> shape:
/// <see cref="IHttpClientFactory"/> with a named client, non-throwing
/// failure semantics. The caching decorator owns TTL behavior; this
/// class is the live-fetch path only.
/// </summary>
public class HttpProviderMembershipClient : IProviderMembershipClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpProviderMembershipClient> _logger;

    public HttpProviderMembershipClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpProviderMembershipClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NetworkMembership?> GetMembershipAsync(
        string tenantId,
        string networkId,
        string npi,
        DateTime asOf,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        // forceRefresh is honored by the caching decorator; the live
        // path always issues the call and is unaware of cache semantics.
        _ = forceRefresh;

        if (string.IsNullOrWhiteSpace(networkId) || string.IsNullOrWhiteSpace(npi))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.ProviderService);
            var encodedNetwork = Uri.EscapeDataString(networkId);
            var encodedNpi = Uri.EscapeDataString(npi);
            var asOfQuery = asOf.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);

            var url = $"/api/v1/networks/{encodedNetwork}/members/{encodedNpi}" +
                      $"?asOf={Uri.EscapeDataString(asOfQuery)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 404 = NPI has no participation row in this network.
                // Surface as a non-null "not member" snapshot so the
                // enforcement stage can distinguish "definitely not a
                // member" from "lookup degraded" (which returns null).
                return new NetworkMembership
                {
                    NetworkId = networkId,
                    Npi = npi,
                    IsActiveMember = false,
                    AsOfDate = asOf,
                    ParticipationStatus = "not_a_member",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Provider service returned {StatusCode} resolving membership for network {Network} NPI {Npi}",
                    response.StatusCode, SanitizeForLog(networkId), SanitizeForLog(npi));
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<NetworkMembership>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            _logger.LogWarning(ex,
                "Membership lookup failed for network {Network} NPI {Npi} tenant {Tenant}",
                SanitizeForLog(networkId), SanitizeForLog(npi), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
