using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PaymentService.Services;

/// <summary>
/// Typed HTTP client for trading-partner-service. Consumed by
/// <c>PaymentRunService</c> (5.10) to resolve which trading partner
/// receives the 835 envelope for each claim's billing-provider NPI.
/// Resolution is per-PaymentRun-execution and cached locally for the
/// duration of the run; trading-partner config is stable enough that
/// run-scoped caching is sufficient (mirrors the 1-hour TTL on 5.6's
/// credentialing client) without requiring a singleton cache surface.
/// </summary>
public interface ITradingPartnersClient
{
    /// <summary>
    /// Resolve the trading partner that handles ERAs for the given
    /// billing-provider NPI within (tenantId, environment). Returns
    /// null on 404 or any non-success response — callers should treat
    /// missing trading partners as a soft failure (log a warning, skip
    /// the claim, surface in PaymentRun.Warnings).
    /// </summary>
    Task<TradingPartnerSummary?> GetByBillingProviderNpiAsync(
        string tenantId,
        string npi,
        string environment,
        CancellationToken ct = default);
}

/// <summary>
/// Subset of <c>CloudHealthOffice.TradingPartnerService.Models.TradingPartner</c>
/// exposed to payment-service. Carries the X12 envelope identifiers
/// needed for ISA/GS construction. BPR banking detail still flows from
/// payment-service configuration (Era:Payer*/Payee* keys) — Phase 1
/// does not surface bank fields on the trading-partner-service API.
/// </summary>
public class TradingPartnerSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("tradingPartnerId")]
    public string TradingPartnerId { get; set; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = string.Empty;

    [JsonPropertyName("partnerName")]
    public string PartnerName { get; set; } = string.Empty;

    [JsonPropertyName("x12Config")]
    public X12ConfigDto? X12Config { get; set; }

    [JsonPropertyName("billingProviderNpis")]
    public List<string> BillingProviderNpis { get; set; } = new();
}

public class X12ConfigDto
{
    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("receiverId")]
    public string ReceiverId { get; set; } = string.Empty;

    [JsonPropertyName("isaQualifier")]
    public string IsaQualifier { get; set; } = "ZZ";

    [JsonPropertyName("testIndicator")]
    public string TestIndicator { get; set; } = "P";
}

public class TradingPartnersClient : ITradingPartnersClient
{
    public const string HttpClientName = "TradingPartnerService";

    private readonly HttpClient _http;
    private readonly ILogger<TradingPartnersClient> _logger;

    public TradingPartnersClient(IHttpClientFactory httpClientFactory, ILogger<TradingPartnersClient> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _logger = logger;
    }

    public async Task<TradingPartnerSummary?> GetByBillingProviderNpiAsync(
        string tenantId,
        string npi,
        string environment,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(npi) || string.IsNullOrEmpty(environment))
            return null;

        try
        {
            var path = $"/api/tradingpartners/by-npi/{Uri.EscapeDataString(tenantId)}/{Uri.EscapeDataString(npi)}/{Uri.EscapeDataString(environment)}";
            var response = await _http.GetAsync(path, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Trading partner lookup miss: tenant {Tenant} npi {Npi} env {Env}",
                    Sanitize(tenantId), Sanitize(npi), Sanitize(environment));
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Trading partner lookup failed for tenant {Tenant} npi {Npi}: {Status}",
                    Sanitize(tenantId), Sanitize(npi), response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TradingPartnerSummary>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Trading partner lookup threw for tenant {Tenant} npi {Npi}",
                Sanitize(tenantId), Sanitize(npi));
            return null;
        }
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
