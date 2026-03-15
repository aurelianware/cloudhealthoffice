using Microsoft.AspNetCore.Http;

namespace CloudHealthOffice.Infrastructure.Middleware;

public static class HttpContextExtensions
{
    /// <summary>
    /// Retrieves the current tenant ID from HttpContext, as set by <see cref="TenantMiddleware"/>.
    /// </summary>
    /// <exception cref="TenantContextMissingException">Thrown when TenantMiddleware has not run or tenant ID is missing.</exception>
    public static string GetTenantId(this HttpContext context)
    {
        return context.Items["TenantId"]?.ToString()
            ?? throw new TenantContextMissingException(
                "Tenant context not found. Ensure TenantMiddleware is registered in the pipeline.");
    }

    /// <summary>
    /// Tries to retrieve the current tenant ID. Returns null if not available.
    /// </summary>
    public static string? GetTenantIdOrDefault(this HttpContext context)
    {
        return context.Items["TenantId"]?.ToString();
    }
}

/// <summary>
/// Thrown when tenant context is required but not available in HttpContext.
/// Maps to HTTP 401 Unauthorized via <see cref="ExceptionHandlingMiddleware"/>.
/// </summary>
public class TenantContextMissingException : Exception
{
    public TenantContextMissingException() : base("Tenant context is missing.") { }
    public TenantContextMissingException(string message) : base(message) { }
    public TenantContextMissingException(string message, Exception innerException) : base(message, innerException) { }
}
