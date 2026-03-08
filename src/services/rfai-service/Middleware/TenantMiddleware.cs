namespace RfaiService.Middleware;

/// <summary>
/// Extracts TenantId from a JWT claim or request header and stores it in
/// <c>HttpContext.Items["TenantId"]</c> for downstream repository use.
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
        if (IsHealthCheckPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        string? tenantId = null;

        // 1. Try JWT claims (production)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            tenantId = context.User.FindFirst("tenant_id")?.Value
                      ?? context.User.FindFirst("extension_TenantId")?.Value;
        }

        // 2. Fallback to headers (development / service-to-service)
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault()
                      ?? context.Request.Headers["X-Dev-Tenant-ID"].FirstOrDefault();
        }

        // 3. Default for local development
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = "default-tenant";
            _logger.LogWarning("No TenantId found in JWT or headers, using default: {TenantId}", tenantId);
        }

        context.Items["TenantId"] = tenantId;

        _logger.LogInformation("Request tenant: {TenantId}", tenantId);

        await _next(context);
    }

    private static bool IsHealthCheckPath(PathString path)
        => path.StartsWithSegments("/health")
        || path.StartsWithSegments("/ready")
        || path.StartsWithSegments("/live");
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder builder)
        => builder.UseMiddleware<TenantMiddleware>();
}
