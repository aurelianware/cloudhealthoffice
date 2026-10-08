using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="IFeeSchedulePricingClient"/> calling
/// benefit-plan-service's <c>POST /api/v1/adjudication/resolve-rates</c>.
/// Sibling of <see cref="HttpProviderIntegrityClient"/> in shape: reuses the
/// <see cref="UpstreamClientNames.BenefitPlanService"/> named client (base
/// address <c>Services:BenefitPlanService</c>, timeout
/// <c>Services:BenefitPlanServiceTimeoutSeconds</c>) and never throws on
/// transport failure — it returns <c>null</c> so the stage can pend.
/// </summary>
public class HttpFeeSchedulePricingClient : IFeeSchedulePricingClient
{
    internal const string ResolveRatesPath = "/api/v1/adjudication/resolve-rates";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpFeeSchedulePricingClient> _logger;

    public HttpFeeSchedulePricingClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpFeeSchedulePricingClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PricingResultSet?> ResolveBatchAsync(
        string tenantId,
        IReadOnlyList<PricingRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);

            using var request = new HttpRequestMessage(HttpMethod.Post, ResolveRatesPath)
            {
                Content = JsonContent.Create(requests, options: JsonOptions),
            };
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Benefit-plan-service resolve-rates returned {StatusCode} for tenant {Tenant} ({LineCount} lines)",
                    response.StatusCode, SanitizeForLog(tenantId), requests.Count);
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<PricingResultSet>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex,
                "Fee schedule pricing failed for tenant {Tenant} ({LineCount} lines)",
                SanitizeForLog(tenantId), requests.Count);
            return null;
        }
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
