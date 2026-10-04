using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Middleware;

namespace PaymentService.Services;

/// <summary>
/// The claims-service calls payment and reversal runs make. Every call names
/// the run's tenant in <c>X-Tenant-ID</c>: inside a request the shared
/// <c>ChoOutboundTokenHandler</c> forwards the caller's token (and echoes the
/// token tenant), and with no caller it mints a payment-service token for this
/// tenant. Without the header a call from a background job would go out with no
/// token and claims-service would answer 401. Responses are returned as is; the
/// runs decide what a failure means.
/// </summary>
public interface IClaimsServiceClient
{
    /// <summary>GET /api/claims/search?{query}.</summary>
    Task<HttpResponseMessage> SearchClaimsAsync(string tenantId, string query, CancellationToken ct = default);

    /// <summary>GET /api/claims/{claimId}.</summary>
    Task<HttpResponseMessage> GetClaimAsync(string tenantId, string claimId, CancellationToken ct = default);

    /// <summary>POST /api/claims/{claimId}/remittance (finalize a paid claim).</summary>
    Task<HttpResponseMessage> PostRemittanceAsync(string tenantId, string claimId, object body, CancellationToken ct = default);

    /// <summary>POST /api/claims/{claimId}/void (reversal of a paid claim).</summary>
    Task<HttpResponseMessage> VoidClaimAsync(string tenantId, string claimId, object body, CancellationToken ct = default);

    /// <summary>GET /api/v1/adjustments?{query}.</summary>
    Task<HttpResponseMessage> ListAdjustmentsAsync(string tenantId, string query, CancellationToken ct = default);

    /// <summary>GET /api/v1/adjustments/{adjustmentId}.</summary>
    Task<HttpResponseMessage> GetAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default);
}

public sealed class ClaimsServiceClient : IClaimsServiceClient
{
    /// <summary>Name of the factory client (base address from ClaimsService:BaseUrl).</summary>
    public const string HttpClientName = "ClaimsService";

    private readonly HttpClient _http;

    public ClaimsServiceClient(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
    }

    public Task<HttpResponseMessage> SearchClaimsAsync(string tenantId, string query, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"/api/claims/search?{query}", tenantId, null, ct);

    public Task<HttpResponseMessage> GetClaimAsync(string tenantId, string claimId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"/api/claims/{Uri.EscapeDataString(claimId)}", tenantId, null, ct);

    public Task<HttpResponseMessage> PostRemittanceAsync(string tenantId, string claimId, object body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"/api/claims/{Uri.EscapeDataString(claimId)}/remittance", tenantId, body, ct);

    public Task<HttpResponseMessage> VoidClaimAsync(string tenantId, string claimId, object body, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"/api/claims/{Uri.EscapeDataString(claimId)}/void", tenantId, body, ct);

    public Task<HttpResponseMessage> ListAdjustmentsAsync(string tenantId, string query, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"/api/v1/adjustments?{query}", tenantId, null, ct);

    public Task<HttpResponseMessage> GetAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, $"/api/v1/adjustments/{Uri.EscapeDataString(adjustmentId)}", tenantId, null, ct);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string tenantId, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A claims-service call needs the run's tenant; none was recorded on the run.");

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        if (body != null)
            request.Content = JsonContent.Create(body, body.GetType());
        return _http.SendAsync(request, ct);
    }
}
