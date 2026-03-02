namespace EligibilityService.Middleware;

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

        // Extract tenant ID from header
        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var tenantId))
        {
            context.Items["TenantId"] = tenantId.ToString();
            _logger.LogDebug("Tenant ID set to {TenantId}", SanitizeForLog(tenantId.ToString()));
        }
        else
        {
            // Check query parameter
            if (context.Request.Query.TryGetValue("tenantId", out var queryTenantId))
            {
                context.Items["TenantId"] = queryTenantId.ToString();
                _logger.LogDebug("Tenant ID from query: {TenantId}", SanitizeForLog(queryTenantId.ToString()));
            }
            else
            {
                _logger.LogWarning("No tenant ID found in request");
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Tenant ID is required");
                return;
            }
        }

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

public class TenantActionFilter : Microsoft.AspNetCore.Mvc.Filters.IActionFilter
{
    public void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        var tenantId = context.HttpContext.Items["TenantId"]?.ToString();
        
        if (!string.IsNullOrEmpty(tenantId))
        {
            var controller = context.Controller as Controllers.EligibilityController;
            if (controller != null)
            {
                controller.TenantId = tenantId;
            }
        }
    }

    public void OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context)
    {
    }
}
