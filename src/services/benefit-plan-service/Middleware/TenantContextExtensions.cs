namespace BenefitPlanService.Middleware;

/// <summary>
/// Reads the tenant that the shared
/// <c>CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware</c> resolved
/// from the validated token (registered by <c>app.UseChoAuthentication()</c>).
/// The tenant never comes from a header, the query string or a request body.
/// </summary>
public static class TenantContextExtensions
{
    /// <summary>The token tenant for this request, or null when none was established.</summary>
    public static string? GetTenantId(this HttpContext context)
    {
        return context.Items["TenantId"] as string;
    }
}
