namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// HTTP message handler that adds the X-Tenant-ID header to all outgoing requests.
/// This ensures backend microservices can apply tenant-specific data filtering.
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
        // Get tenant ID from current context
        var tenantId = await _tenantContextService.GetTenantIdAsync();

        if (!string.IsNullOrEmpty(tenantId))
        {
            // Add X-Tenant-ID header to request
            request.Headers.Add("X-Tenant-ID", tenantId);
            _logger.LogDebug("Added X-Tenant-ID header: {TenantId} for {RequestUri}", tenantId, request.RequestUri);
        }
        else
        {
            _logger.LogWarning("No tenant ID available for request to {RequestUri}", request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
