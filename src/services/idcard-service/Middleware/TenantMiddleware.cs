namespace IdCardService.Middleware;

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
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Tenant ID is required");
            return;
        }

        await _next(context);
    }

    private static bool IsHealthCheckPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/ready") ||
        path.StartsWithSegments("/live") ||
        path.StartsWithSegments("/swagger");
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseIdCardTenantMiddleware(this IApplicationBuilder builder) =>
        builder.UseMiddleware<TenantMiddleware>();
}

public class TenantActionFilter : Microsoft.AspNetCore.Mvc.Filters.IActionFilter
{
    public void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        var tenantId = context.HttpContext.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId)) return;

        if (context.Controller is Controllers.TenantAwareControllerBase c)
        {
            c.TenantId = tenantId;
        }
    }

    public void OnActionExecuted(Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext context) { }
}
