namespace AccumulatorService.Middleware;

/// <summary>
/// Tenant isolation middleware. Every request must carry X-Tenant-ID (or a
/// tenantId query parameter as a dev convenience); missing tenant → 400. Writes
/// the resolved tenant to HttpContext.Items["TenantId"] for the action filter.
/// Health-check paths bypass.
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
        if (IsBypassPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var tenantId))
        {
            context.Items["TenantId"] = tenantId.ToString();
        }
        else if (context.Request.Query.TryGetValue("tenantId", out var queryTenantId))
        {
            context.Items["TenantId"] = queryTenantId.ToString();
        }
        else
        {
            _logger.LogWarning("No tenant ID found in request to {Path}", SanitizeForLog(context.Request.Path));
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Tenant ID is required");
            return;
        }

        await _next(context);
    }

    private static bool IsBypassPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/ready") ||
        path.StartsWithSegments("/live") ||
        path.StartsWithSegments("/swagger");

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder builder) =>
        builder.UseMiddleware<TenantMiddleware>();
}

public class TenantActionFilter : Microsoft.AspNetCore.Mvc.Filters.IActionFilter
{
    public void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        var tenantId = context.HttpContext.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId)) return;

        if (context.Controller is Controllers.AccumulatorsController c)
        {
            c.TenantId = tenantId;
        }
    }

    public void OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context) { }
}
