namespace AuthorizationService.Middleware;

/// <summary>
/// Middleware to extract TenantId from JWT claims or headers
/// Multi-tenant SaaS isolation: each tenant's authorizations are partitioned by TenantId
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
        string? tenantId = null;

        // 1. Try to get from JWT claims (production)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Azure AD B2C or custom JWT
            tenantId = context.User.FindFirst("tenant_id")?.Value
                      ?? context.User.FindFirst("extension_TenantId")?.Value;
        }

        // 2. Fallback to headers (development/testing)
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

        // Store in HttpContext for repository access
        context.Items["TenantId"] = tenantId;

        _logger.LogInformation("Request authenticated for TenantId: {TenantId}", tenantId);

        await _next(context);
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantMiddleware>();
    }
}
