namespace EncounterService.Middleware;

/// <summary>
/// Middleware to extract TenantId from JWT claims or headers.
/// Multi-tenant SaaS isolation: each tenant's encounters are partitioned by TenantId.
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
            _logger.LogWarning("No TenantId found in JWT or headers, using default: {TenantId}",
                SanitizeForLog(tenantId));
        }

        context.Items["TenantId"] = tenantId;
        _logger.LogInformation("Request authenticated for TenantId: {TenantId}",
            SanitizeForLog(tenantId));

        await _next(context);
    }

    private static bool IsHealthCheckPath(PathString path)
    {
        return path.StartsWithSegments("/health") ||
               path.StartsWithSegments("/ready") ||
               path.StartsWithSegments("/live");
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantMiddleware>();
    }
}
