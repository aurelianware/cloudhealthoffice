namespace SmartAuthService.Middleware;

/// <summary>
/// Extracts CHO tenant context for downstream services.
/// Auth endpoints (connect/*, account/*, health) are passthrough.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPassthroughPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var tenantId = ExtractTenantId(context);
        if (!string.IsNullOrEmpty(tenantId))
            context.Items["TenantId"] = tenantId;

        await _next(context);
    }

    private static bool IsPassthroughPath(PathString path)
        => path.StartsWithSegments("/health")
        || path.StartsWithSegments("/connect")
        || path.StartsWithSegments("/account")
        || path.StartsWithSegments("/.well-known");

    private static string? ExtractTenantId(HttpContext context)
    {
        var claim = context.User?.FindFirst("tenant_id")
                 ?? context.User?.FindFirst("extension_TenantId");
        if (claim != null) return claim.Value;

        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var h))
            return h.FirstOrDefault();

        if (context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var dh))
            return dh.FirstOrDefault();

        return null;
    }
}
