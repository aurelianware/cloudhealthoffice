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
/// After a tenant switch, MainLayout updates the DefaultRequestHeaders so all
/// subsequent requests use the new tenant ID automatically.
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
        // in MainLayout), skip dynamic resolution entirely.
        if (request.Headers.Contains("X-Tenant-ID"))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Dynamically resolve the tenant ID via ITenantContextService.
        try
        {
            var tenantId = await _tenantContextService.GetTenantIdAsync();
            if (!string.IsNullOrEmpty(tenantId))
            {
                request.Headers.Add("X-Tenant-ID", tenantId);
            }
            else
            {
                _logger.LogWarning("No tenant ID available for request to {RequestUri}", request.RequestUri);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("GetAuthenticationStateAsync"))
        {
            // Outside Blazor circuit scope — let the request proceed without the header
            _logger.LogDebug(
                "Cannot resolve tenant context outside Razor scope for {RequestUri}, using pre-set header if available",
                request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
