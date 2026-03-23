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
        // Always read current tenant context to support dynamic tenant switching.
        // MainLayout updates DefaultRequestHeaders on switch, but per-request
        // resolution via ITenantContextService ensures correctness.
        try
        {
            var tenantContext = await _tenantContextService.GetCurrentTenantContextAsync();
            if (tenantContext?.TenantId != null)
            {
                request.Headers.Remove("X-Tenant-ID");
                request.Headers.Add("X-Tenant-ID", tenantContext.TenantId);
                return await base.SendAsync(request, cancellationToken);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("GetAuthenticationStateAsync"))
        {
            // Outside Blazor circuit scope — fall through to check DefaultRequestHeaders
            _logger.LogDebug(
                "Cannot resolve tenant context outside Razor scope for {RequestUri}, using pre-set header if available",
                request.RequestUri);
        }

        // Fallback: if dynamic resolution failed (e.g. outside circuit scope),
        // let the pre-set DefaultRequestHeaders pass through
        if (!request.Headers.Contains("X-Tenant-ID"))
        {
            _logger.LogWarning("No tenant ID available for request to {RequestUri}", request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
