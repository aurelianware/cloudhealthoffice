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
        // Health checks don't require authentication or tenant context
        if (IsHealthCheckPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        string? tenantId = null;

        // 1. PRODUCTION: Get from validated JWT claims (after UseAuthentication)
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Custom tenant_id claim (mapped from Azure AD tid + org mapping)
            tenantId = context.User.FindFirst("tenant_id")?.Value
                      ?? context.User.FindFirst("extension_TenantId")?.Value
                      ?? context.User.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
            
            if (!string.IsNullOrEmpty(tenantId))
            {
                _logger.LogInformation("TenantId extracted from JWT: {TenantId} for user {UserId}", 
                    SanitizeForLog(tenantId), SanitizeForLog(context.User.FindFirst("sub")?.Value ?? "unknown"));
            }
        }

        // 2. DEVELOPMENT: Fallback to headers for local testing (requires auth disabled in config)
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault()
                      ?? context.Request.Headers["X-Dev-Tenant-ID"].FirstOrDefault();
            
            if (!string.IsNullOrEmpty(tenantId))
            {
                _logger.LogWarning("TenantId from header (dev mode): {TenantId}", SanitizeForLog(tenantId));
            }
        }

        // 3. FALLBACK: Default for unauthenticated local development only
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = "default-tenant";
            _logger.LogWarning("No TenantId found in JWT or headers, using default: {TenantId}. " +
                              "This should only happen in local development!", SanitizeForLog(tenantId));
        }

        // Store in HttpContext for repository access
        context.Items["TenantId"] = tenantId;

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
