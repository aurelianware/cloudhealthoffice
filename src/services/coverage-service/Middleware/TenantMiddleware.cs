using Microsoft.AspNetCore.Http;

namespace CoverageService.Middleware;

/// <summary>
/// Tenant access for controllers. The tenant is resolved from the validated
/// token by the shared CloudHealthOffice.Infrastructure TenantMiddleware,
/// registered through <c>UseChoAuthentication()</c>.
/// </summary>
public static class TenantMiddlewareExtensions
{
    public static string GetTenantId(this HttpContext context)
    {
        return context.Items["TenantId"]?.ToString()
            ?? throw new InvalidOperationException("Tenant context not found. Ensure UseChoAuthentication() is registered.");
    }
}
