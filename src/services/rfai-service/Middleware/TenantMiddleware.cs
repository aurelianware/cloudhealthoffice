namespace RfaiService.Middleware;

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

        if (context.User.Identity?.IsAuthenticated == true)
        {
            tenantId = context.User.FindFirst("tenant_id")?.Value
                      ?? context.User.FindFirst("extension_TenantId")?.Value;
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault()
                      ?? context.Request.Headers["X-Dev-Tenant-ID"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = "default-tenant";
            _logger.LogWarning("No TenantId found — using default");
        }

        context.Items["TenantId"] = tenantId;
        await _next(context);
    }

    private static bool IsHealthCheckPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/ready") ||
        path.StartsWithSegments("/live");
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder builder) =>
        builder.UseMiddleware<TenantMiddleware>();
}
