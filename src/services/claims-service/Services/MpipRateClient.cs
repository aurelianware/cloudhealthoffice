using System.Net.Http.Json;
using System.Text.Json;

namespace ClaimsService.Services;

/// <summary>
/// HTTP client for the provider-service MPIP rate-check endpoint.
/// Used during adjudication to determine if the FL SMMC 3.0 106.3%
/// Medicare multiplier applies.
/// </summary>
public interface IMpipRateClient
{
    /// <summary>
    /// Get the MPIP enhanced rate multiplier for a provider/service/member combination.
    /// Returns 1.0m if the provider-service is unavailable or MPIP does not apply.
    /// </summary>
    Task<decimal> GetMultiplierAsync(
        string providerId, string tenantId,
        DateTime serviceDate, int memberAge);
}

/// <summary>
/// Calls <c>GET /api/mpip/{tenantId}/rate-check</c> on the provider-service
/// to retrieve the applicable MPIP multiplier.
/// </summary>
public class MpipRateClient : IMpipRateClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MpipRateClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public MpipRateClient(
        IHttpClientFactory httpClientFactory,
        ILogger<MpipRateClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<decimal> GetMultiplierAsync(
        string providerId, string tenantId,
        DateTime serviceDate, int memberAge)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ProviderService");
            var url = $"/api/mpip/{Uri.EscapeDataString(tenantId)}/rate-check" +
                      $"?providerId={Uri.EscapeDataString(providerId)}" +
                      $"&serviceDate={serviceDate:yyyy-MM-dd}" +
                      $"&memberAge={memberAge}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<MpipRateCheckResponse>(JsonOptions);
                return result?.Multiplier ?? 1.0m;
            }

            _logger.LogWarning(
                "MPIP rate check returned {StatusCode} for provider {ProviderId}, " +
                "defaulting to 1.0x",
                response.StatusCode, providerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MPIP rate check failed for provider {ProviderId}, defaulting to 1.0x",
                providerId);
        }

        return 1.0m;
    }
}

internal class MpipRateCheckResponse
{
    public decimal Multiplier { get; set; } = 1.0m;
    public bool EnhancedRateApplies { get; set; }
}
