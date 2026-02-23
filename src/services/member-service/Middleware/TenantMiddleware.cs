using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace MemberService.Middleware;

/// <summary>
/// Extracts tenant context from JWT tokens or headers for multi-tenant isolation.
/// Identical implementation to Sponsor Service for consistency.
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

        context.Items["TenantId"] = tenantId;
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
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var tenantClaim = user.FindFirst("tenant_id")?.Value
                           ?? user.FindFirst("extension_TenantId")?.Value
                           ?? user.FindFirst(ClaimTypes.GroupSid)?.Value;

            if (!string.IsNullOrEmpty(tenantClaim))
            {
                return tenantClaim;
            }
        }

        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            return headerValue.FirstOrDefault();
        }

        if (context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var devHeaderValue))
        {
            return devHeaderValue.FirstOrDefault();
        }

        return null;
    }
}

public static class TenantMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantMiddleware>();
    }

    public static string GetTenantId(this HttpContext context)
    {
        return context.Items["TenantId"]?.ToString() 
            ?? throw new InvalidOperationException("Tenant context not found. Ensure TenantMiddleware is registered.");
    }
}
