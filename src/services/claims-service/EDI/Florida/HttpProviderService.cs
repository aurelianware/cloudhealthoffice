using System.Net.Http.Json;
using System.Text.Json;

namespace ClaimsService.EDI.Florida;

/// <summary>
/// HTTP-backed implementation of <see cref="IProviderService"/> that calls the
/// provider-service to retrieve a provider's Florida Medicaid Provider Number.
/// </summary>
public class HttpProviderService : IProviderService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpProviderService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public HttpProviderService(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpProviderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> GetFloridaMedicaidProviderIdAsync(string npi, string tenantId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ProviderService");
            var url = $"/api/providers/npi/{Uri.EscapeDataString(npi)}/florida-medicaid-id";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content
                    .ReadFromJsonAsync<FloridaMedicaidIdResponse>(JsonOptions);
                return result?.FloridaMedicaidProviderId;
            }

            _logger.LogWarning(
                "Provider service returned {StatusCode} for NPI {Npi}",
                response.StatusCode, npi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve Florida Medicaid Provider ID for NPI {Npi}", npi);
        }

        return null;
    }

    private class FloridaMedicaidIdResponse
    {
        public string? FloridaMedicaidProviderId { get; set; }
    }
}
