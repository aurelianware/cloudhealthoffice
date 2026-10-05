using System.Diagnostics;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Models;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Middleware;

/// <summary>
/// Establishes the tenant a request acts within, from the validated token only.
///
/// TENANT IS AUTHORITY. The only statement of tenancy CHO accepts is a tenant
/// claim inside a token whose signature, issuer, audience and lifetime the
/// authentication handler has already validated. Specifically:
///
///   * A token with no tenant claim is rejected with 401. There is no
///     "default-tenant" fallback and no header fallback.
///   * An <c>X-Tenant-ID</c> header is tolerated only as an echo of the token's
///     tenant. A header that names a different tenant is rejected with 403 and
///     logged, because the request's own two statements of authority disagree.
///   * <c>X-Dev-Tenant-ID</c> is never honoured.
///
/// Unauthenticated requests are passed through untouched so the authorization
/// middleware (which runs next) can issue the 401 challenge, or serve an
/// endpoint explicitly marked <c>[AllowAnonymous]</c>.
/// </summary>
public class TenantMiddleware
{
    public const string TenantHeaderName = "X-Tenant-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;
    private readonly TenantMiddlewareOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            // No identity, so no tenant. Authorization decides whether the
            // endpoint may be served anonymously; it never gets a tenant.
            await _next(context);
            return;
        }

        var tokenTenant = ResolveTokenTenant(context, _options);
        var headerTenant = context.Request.Headers.TryGetValue(TenantHeaderName, out var header)
            ? header.FirstOrDefault()
            : null;

        if (string.IsNullOrEmpty(tokenTenant))
        {
            _logger.LogWarning(
                "Authenticated request without a tenant claim rejected for {Path}",
                SanitizeForLog(context.Request.Path));
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "TENANT_CONTEXT_MISSING",
                "The access token does not carry a tenant claim.");
            return;
        }

        if (!string.IsNullOrEmpty(headerTenant) &&
            !string.Equals(headerTenant, tokenTenant, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Tenant conflict rejected: token asserts {TokenTenant}, {Header} header asserts {HeaderTenant}, subject {Subject}, path {Path}",
                SanitizeForLog(tokenTenant), TenantHeaderName, SanitizeForLog(headerTenant),
                SanitizeForLog(context.User.FindFirst(ChoClaimTypes.Subject)?.Value),
                SanitizeForLog(context.Request.Path));
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "TENANT_CONTEXT_CONFLICT",
                "The tenant header does not match the tenant in the access token.");
            return;
        }

        context.Items["TenantId"] = tokenTenant;
        await _next(context);
    }

    internal static string? ResolveTokenTenant(HttpContext context, TenantMiddlewareOptions options)
    {
        foreach (var claimType in options.TenantClaimTypes)
        {
            var value = context.User.FindFirst(claimType)?.Value;
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

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var error = new StandardErrorResponse
        {
            Code = code,
            Message = message,
            TraceId = Activity.Current?.Id ?? context.TraceIdentifier
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(error, JsonOptions));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Extension methods for registering <see cref="TenantMiddleware"/>.
/// </summary>
public static class TenantMiddlewareExtensions
{
    /// <summary>
    /// Adds <see cref="TenantMiddleware"/> to the application pipeline. Must run
    /// after <c>UseAuthentication()</c>; prefer
    /// <see cref="ChoAuthenticationExtensions.UseChoAuthentication"/>, which
    /// orders authentication, tenant resolution and authorization correctly.
    /// </summary>
    public static IApplicationBuilder UseTenantMiddleware(this IApplicationBuilder builder)
        => builder.UseMiddleware<TenantMiddleware>();
}

/// <summary>
/// Configuration options for <see cref="TenantMiddleware"/>.
/// </summary>
public class TenantMiddlewareOptions
{
    /// <summary>
    /// Token claim types that carry the CHO tenant, in precedence order.
    /// </summary>
    public List<string> TenantClaimTypes { get; set; } =
    [
        ChoClaimTypes.TenantId,
        "extension_TenantId"
    ];

    /// <summary>
    /// Request paths that bypass tenant resolution and authorization
    /// (liveness/readiness probes and the Prometheus scrape endpoint).
    /// </summary>
    public List<string> PassthroughPaths { get; set; } =
    [
        "/health",
        "/ready",
        "/live",
        "/metrics"
    ];
}
