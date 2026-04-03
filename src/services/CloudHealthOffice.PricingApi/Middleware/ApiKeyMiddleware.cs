using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;

namespace CloudHealthOffice.PricingApi.Middleware;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    // Paths that don't require an API key
    private static readonly string[] ExemptPaths =
    [
        "/health",
        "/swagger",
        "/api/v1/fee-schedules",  // Allow browsing available schedules without auth
        "/api/v1/lookup",         // Public single-code lookup (web demo, no signup required)
        "/api/v1/admin",          // Admin endpoints use X-Admin-Secret instead of API key
        "/api/v1/signup"          // Public self-service signup
    ];

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip auth for exempt paths (segment-aware to avoid matching e.g. /api/v1/lookup2)
        var requestPath = context.Request.Path;
        if (ExemptPaths.Any(p => requestPath.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        // Skip non-API paths
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey))
        {
            _logger.LogWarning("API request without API key from {IP}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError
                {
                    Code = "MISSING_API_KEY",
                    Message = $"Provide your API key in the {ApiKeyHeaderName} header. Register at https://cloudhealthoffice.com/pricing-api to get a free key."
                }
            });
            return;
        }

        var apiKeyRepo = context.RequestServices.GetRequiredService<IApiKeyRepository>();
        var apiKeyRecord = await apiKeyRepo.GetByKeyAsync(extractedApiKey.ToString());

        if (apiKeyRecord is null || !apiKeyRecord.IsActive)
        {
            _logger.LogWarning("Invalid API key attempt: {Key}", extractedApiKey.ToString()[..8] + "...");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError
                {
                    Code = "INVALID_API_KEY",
                    Message = "The provided API key is invalid or has been deactivated."
                }
            });
            return;
        }

        // Check usage limits
        var monthlyLimit = apiKeyRecord.Tier switch
        {
            PricingTier.Free => 1_000,
            PricingTier.Starter => 10_000,
            PricingTier.Professional => 100_000,
            PricingTier.Enterprise => int.MaxValue,
            _ => 1_000
        };

        if (apiKeyRecord.CurrentMonthUsage >= monthlyLimit)
        {
            _logger.LogWarning("Rate limit exceeded for tenant {Tenant}", apiKeyRecord.TenantName);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.Append("X-RateLimit-Limit", monthlyLimit.ToString());
            context.Response.Headers.Append("X-RateLimit-Remaining", "0");
            await context.Response.WriteAsJsonAsync(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError
                {
                    Code = "RATE_LIMIT_EXCEEDED",
                    Message = $"Monthly limit of {monthlyLimit:N0} claims reached for your {apiKeyRecord.Tier} plan. Upgrade at https://cloudhealthoffice.com/pricing-api/upgrade"
                }
            });
            return;
        }

        // Store tenant context for downstream use
        context.Items["ApiKeyRecord"] = apiKeyRecord;
        context.Items["TenantName"] = apiKeyRecord.TenantName;

        // Add rate limit headers
        context.Response.Headers.Append("X-RateLimit-Limit", monthlyLimit.ToString());
        context.Response.Headers.Append("X-RateLimit-Remaining", (monthlyLimit - apiKeyRecord.CurrentMonthUsage).ToString());

        await _next(context);
    }
}

public static class ApiKeyMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder builder)
        => builder.UseMiddleware<ApiKeyMiddleware>();
}
