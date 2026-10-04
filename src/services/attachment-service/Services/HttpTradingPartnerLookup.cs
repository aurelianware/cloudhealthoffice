using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AttachmentService.Models;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace AttachmentService.Services;

/// <summary>
/// Reads a tenant's trading partners from trading-partner-service
/// (<c>GET api/TradingPartners/tenant/{tenantId}</c>), which owns the
/// <c>TradingPartners</c> collection. attachment-service no longer opens that
/// collection itself.
/// <para>
/// The call carries attachment-service's own service token for the tenant
/// (its acknowledgment action is already authorized by attachments:write, and
/// the users who generate acknowledgments do not all hold
/// trading-partners:read), but only to a configured CHO host
/// (<see cref="ChoOutboundHosts"/>); without a service token configured the
/// shared handler forwards the caller's token instead.
/// </para>
/// <para>
/// No active partner with the payer id: <c>null</c> (the default 999
/// acknowledgment applies, as before). Any failure to read the partners
/// (401/403, 5xx, transport, unreadable body) throws
/// <see cref="TradingPartnerLookupException"/>: an acknowledgment is never
/// built from made-up interchange ids because the lookup failed.
/// </para>
/// </summary>
public sealed class HttpTradingPartnerLookup : ITradingPartnerLookup
{
    public const string ClientName = "TradingPartnerService";
    public const string DefaultBaseUrl = "http://trading-partner-service.cloudhealthoffice";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpTradingPartnerLookup> _logger;

    public HttpTradingPartnerLookup(
        IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpTradingPartnerLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<TradingPartner?> GetByPayerIdAsync(string payerId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A tenant is required to read trading partners.");

        var client = _httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/TradingPartners/tenant/{Uri.EscapeDataString(tenantId)}");
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        AttachServiceToken(request, client, tenantId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "trading-partner-service unreachable for tenant {TenantId}", Sanitize(tenantId));
            throw new TradingPartnerLookupException("trading-partner-service unreachable", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("trading-partner-service answered {Status} for tenant {TenantId}",
                    (int)response.StatusCode, Sanitize(tenantId));
                throw new TradingPartnerLookupException($"trading-partner-service answered {(int)response.StatusCode}");
            }

            List<PartnerDto>? partners;
            try
            {
                partners = await response.Content.ReadFromJsonAsync<List<PartnerDto>>(Json);
            }
            catch (JsonException ex)
            {
                throw new TradingPartnerLookupException("trading-partner-service returned an unreadable partner list", ex);
            }

            var match = (partners ?? new List<PartnerDto>()).FirstOrDefault(p =>
                string.Equals(p.TradingPartnerId, payerId, StringComparison.Ordinal)
                && string.Equals(p.Status, "Active", StringComparison.OrdinalIgnoreCase));

            return match is null ? null : new TradingPartner
            {
                Id = match.Id ?? string.Empty,
                TenantId = match.TenantId ?? tenantId,
                PartnerId = match.TradingPartnerId ?? string.Empty,
                PartnerName = match.PartnerName ?? string.Empty,
                IsActive = true,
                InterchangeSenderId = match.X12Config?.SenderId,
                InterchangeReceiverId = match.X12Config?.ReceiverId,
                // trading-partner-service carries no acknowledgment preferences, so the model
                // defaults stand: 999, which is what GetAcknowledgmentType falls back to anyway.
            };
        }
    }

    private void AttachServiceToken(HttpRequestMessage request, HttpClient client, string tenantId)
    {
        var issuer = _services.GetService<ChoTokenIssuer>();
        var clientId = _services.GetService<ChoAuthOptions>()?.ServiceToken?.ClientId;
        var target = client.BaseAddress is null ? request.RequestUri : new Uri(client.BaseAddress, request.RequestUri!);
        if (issuer is null || string.IsNullOrEmpty(clientId)
            || _services.GetService<ChoOutboundHosts>()?.IsChoService(target) != true)
        {
            return; // the shared handler forwards the caller's token (CHO hosts only)
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issuer.IssueServiceToken(clientId, tenantId));
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private sealed class PartnerDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
        [JsonPropertyName("tradingPartnerId")] public string? TradingPartnerId { get; set; }
        [JsonPropertyName("partnerName")] public string? PartnerName { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("x12Config")] public X12Dto? X12Config { get; set; }
    }

    private sealed class X12Dto
    {
        [JsonPropertyName("senderId")] public string? SenderId { get; set; }
        [JsonPropertyName("receiverId")] public string? ReceiverId { get; set; }
    }
}

/// <summary>The tenant's trading partners could not be read.</summary>
public sealed class TradingPartnerLookupException : Exception
{
    public TradingPartnerLookupException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
