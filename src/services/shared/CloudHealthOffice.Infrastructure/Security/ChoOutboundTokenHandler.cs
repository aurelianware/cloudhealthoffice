using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Attaches a bearer token to service-to-service calls.
///
/// <list type="number">
///   <item>Inside an authenticated request, the caller's own token is forwarded,
///   so the callee sees the same user, tenant and permissions (and its audience
///   check applies to the original token).</item>
///   <item>Outside one (message consumers, hosted services), this service mints
///   a short-lived service token for the tenant the outbound request names in
///   <c>X-Tenant-ID</c>. That header is read here, inside the calling service,
///   from code that took the tenant from its own message or record; the callee
///   still takes the tenant only from the token.</item>
/// </list>
///
/// A call that can satisfy neither is sent without credentials and the callee
/// rejects it. Nothing falls back to an unauthenticated success.
/// </summary>
public sealed class ChoOutboundTokenHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IServiceProvider _services;
    private readonly ILogger<ChoOutboundTokenHandler> _logger;

    public ChoOutboundTokenHandler(
        IHttpContextAccessor accessor, IServiceProvider services, ILogger<ChoOutboundTokenHandler> logger)
    {
        _accessor = accessor;
        _services = services;
        _logger = logger;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization == null)
        {
            if (IsChoService(request.RequestUri))
                request.Headers.Authorization = ResolveAuthorization(request);
            else
                _logger.LogDebug(
                    "Outbound call to {Host} is not to a configured CHO host; no CHO token or tenant added.",
                    request.RequestUri?.Host);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// CHO tokens are only ever sent to CHO services, as configured in
    /// <see cref="ChoOutboundHosts"/>. Registered for every factory client, this
    /// guard is what keeps a user's token away from clearinghouses, model
    /// vendors, identity providers and anything misconfigured. With no host
    /// policy registered, nothing is a CHO host.
    /// </summary>
    private bool IsChoService(Uri? uri)
        => _services.GetService<ChoOutboundHosts>()?.IsChoService(uri) == true;

    private AuthenticationHeaderValue? ResolveAuthorization(HttpRequestMessage request)
    {
        var http = _accessor.HttpContext;
        var inbound = http?.Request.Headers.Authorization.ToString();
        if (http?.User?.Identity?.IsAuthenticated == true
            && !string.IsNullOrEmpty(inbound)
            && inbound.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // Echo the authenticated tenant so a stale explicit header on the
            // outbound request cannot drift from the token it travels with.
            if (http.Items["TenantId"] is string tenant)
            {
                request.Headers.Remove(TenantMiddleware.TenantHeaderName);
                request.Headers.Add(TenantMiddleware.TenantHeaderName, tenant);
            }
            return AuthenticationHeaderValue.Parse(inbound);
        }

        var issuer = _services.GetService<ChoTokenIssuer>();
        var options = _services.GetService<ChoAuthOptions>();
        var tenantId = request.Headers.TryGetValues(TenantMiddleware.TenantHeaderName, out var values)
            ? values.FirstOrDefault()
            : null;

        if (issuer != null && options?.ServiceToken != null && !string.IsNullOrEmpty(tenantId))
            return new AuthenticationHeaderValue("Bearer", issuer.IssueServiceToken(options.ServiceToken.ClientId, tenantId));

        _logger.LogWarning(
            "Outbound call to {Host} has no user token to forward and no service token could be minted " +
            "(service token configured: {Configured}, tenant named: {HasTenant}); the callee will reject it.",
            request.RequestUri?.Host, issuer != null, !string.IsNullOrEmpty(tenantId));
        return null;
    }
}

public static class ChoOutboundTokenHandlerExtensions
{
    /// <summary>Authenticates calls made through this client to other CHO services.</summary>
    public static IHttpClientBuilder AddChoServiceAuthentication(this IHttpClientBuilder builder)
        => builder.AddHttpMessageHandler<ChoOutboundTokenHandler>();
}
