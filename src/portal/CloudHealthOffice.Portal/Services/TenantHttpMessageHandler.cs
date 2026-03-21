namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// HTTP message handler that adds the X-Tenant-ID header to all outgoing requests.
/// This ensures backend microservices can apply tenant-specific data filtering.
///
/// In Blazor Server the preferred mechanism is for MainLayout to set the header on
/// HttpClient.DefaultRequestHeaders once the tenant context is resolved inside the
/// Razor component DI scope.  This handler acts as a safety-net: it checks whether
/// the header was already set and only attempts dynamic resolution as a fallback.
///
/// Dynamic resolution can fail when IHttpClientFactory resolves this handler in its
/// own DI scope (separate from the Blazor circuit), because
/// AuthenticationStateProvider is only valid inside a Razor component scope.
/// </summary>
public class TenantHttpMessageHandler : DelegatingHandler
{
    private readonly ITenantContextService _tenantContextService;
    private readonly ILogger<TenantHttpMessageHandler> _logger;

    public TenantHttpMessageHandler(
        ITenantContextService tenantContextService,
        ILogger<TenantHttpMessageHandler> logger)
    {
        _tenantContextService = tenantContextService;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // If the header was already set (e.g. via HttpClient.DefaultRequestHeaders
        // from MainLayout), skip resolution entirely.
        if (request.Headers.Contains("X-Tenant-ID"))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Fallback: try to resolve from ITenantContextService.  This may fail when
        // the handler runs outside the Blazor circuit DI scope.
        try
        {
            var tenantId = await _tenantContextService.GetTenantIdAsync();

            if (!string.IsNullOrEmpty(tenantId))
            {
                request.Headers.Add("X-Tenant-ID", tenantId);
                _logger.LogDebug("Added X-Tenant-ID header: {TenantId} for {RequestUri}", tenantId, request.RequestUri);
            }
            else
            {
                _logger.LogWarning("No tenant ID available for request to {RequestUri}", request.RequestUri);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("GetAuthenticationStateAsync"))
        {
            _logger.LogWarning(
                "Cannot resolve tenant ID outside Razor component scope for {RequestUri}. " +
                "Ensure MainLayout sets X-Tenant-ID on HttpClient.DefaultRequestHeaders.",
                request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
