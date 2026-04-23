using FhirService.Middleware;

namespace FhirService.Services;

/// <summary>
/// DelegatingHandler that stamps every outbound request with the current
/// request's tenant id. Required because the CHO services downstream of
/// fhir-service (appeals-service, consent-service, personal-rep-service)
/// enforce tenant isolation by reading <c>X-Tenant-ID</c> from the
/// incoming request. Without this handler, the outbound HttpClient
/// request would be tenant-less and the downstream service would 401.
///
/// Attached to the ChoAppealsService named client alongside
/// <see cref="CorrelationIdPropagationHandler"/>.
/// </summary>
public sealed class TenantHeaderPropagationHandler : DelegatingHandler
{
    public const string HeaderName = "X-Tenant-ID";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantHeaderPropagationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tenantId = _httpContextAccessor.HttpContext?.GetTenantId();
        if (!string.IsNullOrEmpty(tenantId))
        {
            request.Headers.Remove(HeaderName);
            request.Headers.Add(HeaderName, tenantId);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
