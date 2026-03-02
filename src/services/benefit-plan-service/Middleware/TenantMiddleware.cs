using System.Security.Claims;

namespace BenefitPlanService.Middleware;

/// <summary>
/// Middleware to extract and validate tenant context from JWT or headers
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

        var tenantId = ExtractTenantId(context);

        if (string.IsNullOrEmpty(tenantId))
        {
            _logger.LogWarning("Request missing tenant context");
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Missing tenant context" });
            return;
        }

        // Set tenant context for downstream use
        context.Items["TenantId"] = tenantId;
        _logger.LogInformation("Request from tenant: {TenantId}", SanitizeForLog(tenantId));

        await _next(context);
    }

    private static bool IsHealthCheckPath(PathString path)
    {
        return path.StartsWithSegments("/health") ||
               path.StartsWithSegments("/ready") ||
               path.StartsWithSegments("/live");
    }

    private string? ExtractTenantId(HttpContext context)
    {
        // 1. Try to get from JWT claim (preferred for user requests)
        var tenantClaim = context.User?.FindFirst("tenant_id") ?? 
                         context.User?.FindFirst("extension_TenantId");
        if (tenantClaim != null)
        {
            return tenantClaim.Value;
        }

        // 2. Fall back to X-Tenant-ID header (for service-to-service calls)
        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            return headerValue.FirstOrDefault();
        }

        // 3. Development mode: Allow X-Dev-Tenant-ID for local testing
        if (context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var devHeaderValue))
        {
            return devHeaderValue.FirstOrDefault();
        }

        return null;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Extension methods for TenantMiddleware
/// </summary>
public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantMiddleware>();
    }

    public static string? GetTenantId(this HttpContext context)
    {
        return context.Items["TenantId"] as string;
    }
}
