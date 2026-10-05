using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Security;
using SharedTenantMiddleware = CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware;

namespace FhirService.Services.Identity;

/// <summary>
/// Keeps SMART tokens inside fhir-service.
///
/// A SMART token is a credential an external client was given for the FHIR
/// API. It means nothing to CHO services, and forwarding it would hand a third
/// party's credential to every service fhir-service calls. So when the caller
/// is SMART, a call to a CHO service carries fhir-service's own service token
/// for the caller's authenticated tenant (named in <c>X-Tenant-ID</c>), the way
/// idcard-service's scan path acts for provider JWTs. fhir-service has already
/// applied the SMART scopes and patient binding before it makes the call.
///
/// Registered on every IHttpClientFactory client. Its position relative to
/// the shared ChoOutboundTokenHandler does not matter: if it runs first, the
/// service token it sets is left alone (that handler only fills an empty
/// Authorization header); if it runs second, it removes the forwarded SMART
/// token and replaces it. Calls to external hosts never get a service token,
/// and never carry the SMART token either.
///
/// CHO callers and calls with no caller are left to ChoOutboundTokenHandler.
/// </summary>
public sealed class SmartCallerOutboundHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IServiceProvider _services;
    private readonly ILogger<SmartCallerOutboundHandler> _logger;

    public SmartCallerOutboundHandler(
        IHttpContextAccessor accessor, IServiceProvider services, ILogger<SmartCallerOutboundHandler> logger)
    {
        _accessor = accessor;
        _services = services;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var http = _accessor.HttpContext;
        if (http != null && FhirCallerSchemes.IsSmart(http.User))
            await PrepareAsync(request, http, cancellationToken);

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task PrepareAsync(HttpRequestMessage request, HttpContext http, CancellationToken cancellationToken)
    {
        var inbound = http.Request.Headers.Authorization.ToString();
        if (request.Headers.Authorization != null
            && !string.IsNullOrEmpty(inbound)
            && string.Equals(request.Headers.Authorization.ToString(), inbound, StringComparison.Ordinal))
        {
            request.Headers.Authorization = null;
        }

        if (!IsInternal(request.RequestUri))
            return;

        // Whatever an upstream credential was set to, a call to a CHO service on
        // behalf of a SMART caller carries fhir-service's own identity only.
        request.Headers.Authorization = null;
        request.Headers.Remove(SharedTenantMiddleware.TenantHeaderName);

        var tenant = http.Items["TenantId"] as string;
        var tokens = ChoServiceTokens.Resolve(_services);
        if (string.IsNullOrEmpty(tenant) || tokens == null)
        {
            // Sent without credentials; the callee refuses it. Never the SMART token.
            _logger.LogError(
                "A SMART caller's request needs {Host}, but no service token could be minted "
                + "(service token configured: {Configured}, tenant resolved: {HasTenant}).",
                request.RequestUri?.Host, tokens != null, !string.IsNullOrEmpty(tenant));
            return;
        }

        request.Headers.Add(SharedTenantMiddleware.TenantHeaderName, tenant);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await tokens.GetTokenAsync(tenant, cancellationToken));
    }

    /// <summary>The same notion of "a CHO service" as ChoOutboundTokenHandler: the configured allowlist.</summary>
    private bool IsInternal(Uri? uri)
        => _services.GetService<ChoOutboundHosts>()?.IsChoService(uri) == true;
}
