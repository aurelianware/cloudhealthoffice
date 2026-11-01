using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// A Medicaid member's third-party liability resources from coverage-service
/// (<c>GET /api/v1/coverage/member/{id}/tpl?asOfDate=</c>). Consumed by
/// <see cref="Adjudication.Stages.ThirdPartyLiabilityStage"/>.
/// </summary>
public interface ITplClient
{
    /// <summary>The resources covering <paramref name="asOfDate"/>. Empty means
    /// none on file; <c>null</c> means the lookup failed (the stage pends).</summary>
    Task<IReadOnlyList<TplResource>?> GetActiveAsync(
        string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default);
}

/// <summary>Wire shape of coverage-service's <c>TplPolicy</c>, the fields adjudication uses.</summary>
public sealed class TplResource
{
    public string Id { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? CarrierCode { get; set; }
    public string CarrierName { get; set; } = string.Empty;
    public string? PolicyNumber { get; set; }
    public string? GroupNumber { get; set; }
    public string? StateCoverageCode { get; set; }
    public TplServiceScope Scope { get; set; }
    public bool PayAndChaseOnly { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
}

/// <summary>
/// Mirrors coverage-service <c>TplScope</c> (same names, same bits). Read
/// from JSON by name or number.
/// </summary>
[Flags]
public enum TplServiceScope
{
    None = 0,
    Hospital = 1 << 0,
    Professional = 1 << 1,
    Pharmacy = 1 << 2,
    Dental = 1 << 3,
    Vision = 1 << 4,
    BehavioralHealth = 1 << 5,
    LongTermCare = 1 << 6,
    Dme = 1 << 7,
    Casualty = 1 << 8,
    MedicarePartA = 1 << 9,
    MedicarePartB = 1 << 10,
    MedicarePartC = 1 << 11,
    MedicarePartD = 1 << 12,
    Medical = Hospital | Professional | BehavioralHealth | Dme,
    Comprehensive = Medical | Pharmacy,
}

public sealed class HttpTplClient : ITplClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: true) },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpTplClient> _logger;

    public HttpTplClient(IHttpClientFactory httpClientFactory, ILogger<HttpTplClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TplResource>?> GetActiveAsync(
        string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return null;
        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.CoverageService);
            var url = $"/api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/tpl" +
                      $"?asOfDate={Uri.EscapeDataString(asOfDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Coverage service returned {StatusCode} resolving TPL for member {Member}",
                    response.StatusCode, LogSanitizer.MaskId(memberId));
                return null;
            }
            var rows = await response.Content.ReadFromJsonAsync<List<TplResource>>(JsonOptions, ct).ConfigureAwait(false);
            return rows ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "TPL lookup failed for member {Member}", LogSanitizer.MaskId(memberId));
            return null;
        }
    }
}
