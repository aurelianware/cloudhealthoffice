using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace SponsorService.Middleware;

/// <summary>
/// Extracts tenant context from JWT tokens or headers for multi-tenant isolation.
/// Supports Azure AD B2C custom claims and header-based tenant identification.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next)
    {
        _next = next;
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
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Missing tenant context. Provide X-Tenant-ID header or valid JWT with tenant claim.");
            return;
        }

        // Store tenant ID in HttpContext for downstream use
        context.Items["TenantId"] = tenantId;

        // TODO: Validate tenant is active in database
        // var tenantService = context.RequestServices.GetRequiredService<ITenantService>();
        // var isActive = await tenantService.IsTenantActiveAsync(tenantId);
        // if (!isActive) { return 403 Forbidden; }

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
        // Priority 1: JWT claim (Azure AD B2C custom claim)
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            // Check standard tenant_id claim
            var tenantClaim = user.FindFirst("tenant_id")?.Value
                           ?? user.FindFirst("extension_TenantId")?.Value  // B2C custom attribute
                           ?? user.FindFirst(ClaimTypes.GroupSid)?.Value;   // Fallback

            if (!string.IsNullOrEmpty(tenantClaim))
            {
                return tenantClaim;
            }
        }

        // Priority 2: X-Tenant-ID header (for API clients)
        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            return headerValue.FirstOrDefault();
        }

        // Priority 3: X-Dev-Tenant-ID header (local development only)
        if (context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var devHeaderValue))
        {
            // TODO: Only allow in Development environment
            return devHeaderValue.FirstOrDefault();
        }

        return null;
    }
}

/// <summary>
/// Extension methods for TenantMiddleware
/// </summary>
public static class TenantMiddlewareExtensions
{
    /// <summary>
    /// Add tenant context extraction to the request pipeline
    /// </summary>
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantMiddleware>();
    }

    /// <summary>
    /// Get tenant ID from HttpContext.Items
    /// </summary>
    public static string GetTenantId(this HttpContext context)
    {
        return context.Items["TenantId"]?.ToString() 
            ?? throw new InvalidOperationException("Tenant context not found. Ensure TenantMiddleware is registered.");
    }
}
