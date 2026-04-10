using System.Net.Http.Json;
using System.Text.Json;

namespace ClaimsService.EDI.Florida;

/// <summary>
/// HTTP-backed implementation of <see cref="ITenantComplianceConfigService"/> that calls
/// the reference-data-service to retrieve tenant FMMIS compliance configuration.
/// </summary>
public class HttpTenantComplianceConfigService : ITenantComplianceConfigService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpTenantComplianceConfigService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public HttpTenantComplianceConfigService(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpTenantComplianceConfigService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FmmisComplianceConfigDto?> GetConfigAsync(string tenantId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ReferenceDataService");
            var url = $"/api/compliance-config/{Uri.EscapeDataString(tenantId)}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content
                    .ReadFromJsonAsync<FmmisComplianceConfigDto>(JsonOptions);
            }

            _logger.LogWarning(
                "Reference data service returned {StatusCode} for tenant {TenantId} compliance config",
                response.StatusCode, tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to retrieve compliance config for tenant {TenantId}", tenantId);
        }

        return null;
    }
}
