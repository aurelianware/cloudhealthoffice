using System.Net.Http.Json;
using System.Text.Json;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="IProviderIntegrityClient"/> calling
/// benefit-plan-service's <c>GET /api/v1/adjudication/provider-integrity/{npi}</c>.
/// Sibling of <see cref="HttpCredentialingStatusClient"/> in shape:
/// typed factory client, non-throwing on transport failure.
/// </summary>
public class HttpProviderIntegrityClient : IProviderIntegrityClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpProviderIntegrityClient> _logger;

    public HttpProviderIntegrityClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpProviderIntegrityClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ProviderIntegritySnapshot?> CheckAsync(
        string tenantId,
        string npi,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(npi)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);
            var encodedNpi = Uri.EscapeDataString(npi);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/v1/adjudication/provider-integrity/{encodedNpi}");
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Benefit-plan-service returned {StatusCode} resolving provider integrity for NPI {Npi}",
                    response.StatusCode, SanitizeForLog(npi));
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<ProviderIntegritySnapshot>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex,
                "Provider integrity lookup failed for NPI {Npi} tenant {Tenant}",
                SanitizeForLog(npi), SanitizeForLog(tenantId));
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
