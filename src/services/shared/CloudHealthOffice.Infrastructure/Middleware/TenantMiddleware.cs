using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Middleware;

/// <summary>
/// Extracts tenant context from JWT claims or HTTP headers for multi-tenant isolation.
/// Supports both strict (401 on missing) and lenient (default-tenant fallback) modes.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;
    private readonly TenantMiddlewareOptions _options;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger, TenantMiddlewareOptions options)
    {
        _next = next;
        _logger = logger;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPassthroughPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var tenantId = ExtractTenantId(context);

        if (string.IsNullOrEmpty(tenantId))
        {
            if (_options.RequireTenantId)
            {
                _logger.LogWarning("Missing tenant context for {Path}", SanitizeForLog(context.Request.Path));
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync(
                    "Missing tenant context. Provide X-Tenant-ID header or valid JWT with tenant claim.");
                return;
            }

            tenantId = _options.DefaultTenantId;
            _logger.LogWarning("No TenantId found, using default: {TenantId}", SanitizeForLog(tenantId));
        }

        context.Items["TenantId"] = tenantId;
        _logger.LogDebug("Tenant context set: {TenantId}", SanitizeForLog(tenantId));

        await _next(context);
    }

    private string? ExtractTenantId(HttpContext context)
    {
        // 1. Try JWT claims (production)
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var tenantClaim = context.User.FindFirst("tenant_id")?.Value
                           ?? context.User.FindFirst("extension_TenantId")?.Value
                           ?? context.User.FindFirst(ClaimTypes.GroupSid)?.Value;

            if (!string.IsNullOrEmpty(tenantClaim))
                return tenantClaim;
        }

        // 2. Fallback to headers (development/testing)
        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            var value = headerValue.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        if (context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var devHeaderValue))
        {
            var value = devHeaderValue.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }

    private bool IsPassthroughPath(PathString path)
    {
        foreach (var passthrough in _options.PassthroughPaths)
        {
            if (path.StartsWithSegments(passthrough))
                return true;
        }
        return false;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Configuration options for <see cref="TenantMiddleware"/>.
/// </summary>
public class TenantMiddlewareOptions
{
    /// <summary>
    /// If true, returns 401 when no tenant ID can be resolved. If false, falls back to <see cref="DefaultTenantId"/>.
    /// Default: false (lenient mode for backward compatibility).
    /// </summary>
    public bool RequireTenantId { get; set; } = false;

    /// <summary>
    /// Fallback tenant ID when <see cref="RequireTenantId"/> is false and no tenant is found.
    /// Default: "default-tenant".
    /// </summary>
    public string DefaultTenantId { get; set; } = "default-tenant";

    /// <summary>
    /// Request paths that bypass tenant resolution (e.g., health checks, swagger).
    /// Default: /health, /ready, /live, /swagger.
    /// </summary>
    public List<string> PassthroughPaths { get; set; } =
    [
        "/health",
        "/ready",
        "/live",
        "/swagger"
    ];
}
