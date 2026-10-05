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
///   <item>Outside one (message consumers, hosted services), this service sends
///   its own short-lived service token (<see cref="IChoServiceTokenSource"/>:
///   issued by token-service in deployed environments) for the tenant the outbound request names in
///   <c>X-Tenant-ID</c>. That header is read here, inside the calling service,
///   from code that took the tenant from its own message or record; the callee
///   still takes the tenant only from the token.</item>
/// </list>
///
/// The service token is never a substitute for a caller who authenticated some
/// other way (an API key, a provider or SMART token): such a caller holds no
/// CHO token, and lending it this service's identity would let an external
/// client act as the service. Inside a request with an authenticated non-CHO
/// caller, a service token is minted only when the request asks for one with
/// <see cref="ChoOutboundTokenHandlerExtensions.UseChoServiceToken"/>, after
/// the calling code has applied that caller's own limits. A request with no
/// authenticated caller (a signed webhook that verified its own credential)
/// is treated like background work.
///
/// A call that can satisfy none of these is sent without credentials and the
/// callee rejects it. Nothing falls back to an unauthenticated success.
/// </summary>
public sealed class ChoOutboundTokenHandler : DelegatingHandler
{
    /// <summary>
    /// Set on an outbound request (<see cref="ChoOutboundTokenHandlerExtensions.UseChoServiceToken"/>)
    /// to have it carry this service's own token even though the inbound
    /// caller authenticated with a non-CHO credential.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> ServiceTokenRequested = new("cho.service-token-requested");

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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization == null)
        {
            if (IsChoService(request.RequestUri))
                request.Headers.Authorization = await ResolveAuthorizationAsync(request, cancellationToken);
            else
                _logger.LogDebug(
                    "Outbound call to {Host} is not to a configured CHO host; no CHO token or tenant added.",
                    request.RequestUri?.Host);
        }

        return await base.SendAsync(request, cancellationToken);
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

    private async ValueTask<AuthenticationHeaderValue?> ResolveAuthorizationAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
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

        // An authenticated caller without a CHO token (API key, provider JWT,
        // SMART): never lend it this service's identity unless the calling code
        // asked for that explicitly on this request.
        if (http?.User?.Identities.Any(i => i.IsAuthenticated) == true
            && !(request.Options.TryGetValue(ServiceTokenRequested, out var requested) && requested))
        {
            _logger.LogWarning(
                "Outbound call to {Host} made for a caller without a CHO token; no service token is minted " +
                "in its place (request one explicitly with UseChoServiceToken). The callee will reject it.",
                request.RequestUri?.Host);
            return null;
        }

        var source = ChoServiceTokens.Resolve(_services);
        var tenantId = request.Headers.TryGetValues(TenantMiddleware.TenantHeaderName, out var values)
            ? values.FirstOrDefault()
            : null;

        // A source that cannot produce a token (token-service down) throws
        // ChoServiceTokenUnavailableException, an HttpRequestException: the
        // call fails as unavailable instead of going out to be refused.
        if (source != null && !string.IsNullOrEmpty(tenantId))
            return new AuthenticationHeaderValue("Bearer", await source.GetTokenAsync(tenantId, cancellationToken));

        _logger.LogWarning(
            "Outbound call to {Host} has no user token to forward and no service token could be minted " +
            "(service token configured: {Configured}, tenant named: {HasTenant}); the callee will reject it.",
            request.RequestUri?.Host, source != null, !string.IsNullOrEmpty(tenantId));
        return null;
    }
}

public static class ChoOutboundTokenHandlerExtensions
{
    /// <summary>Authenticates calls made through this client to other CHO services.</summary>
    public static IHttpClientBuilder AddChoServiceAuthentication(this IHttpClientBuilder builder)
        => builder.AddHttpMessageHandler<ChoOutboundTokenHandler>();

    /// <summary>
    /// Asks for this service's own token on <paramref name="request"/> when the
    /// inbound caller authenticated without a CHO token. Use it only after the
    /// code has applied that caller's own limits (tenant, scope) itself.
    /// </summary>
    public static HttpRequestMessage UseChoServiceToken(this HttpRequestMessage request)
    {
        request.Options.Set(ChoOutboundTokenHandler.ServiceTokenRequested, true);
        return request;
    }
}
