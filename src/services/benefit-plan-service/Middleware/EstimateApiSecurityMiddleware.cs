using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Middleware;

public sealed class EstimateApiOptions
{
    public const string SectionName = "EstimateApi";
    public bool Enabled { get; set; }
    public bool EstimateOnly { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool IsEstimateOnlyEnabled => Enabled && EstimateOnly;
}

/// <summary>
/// Protects the externally hosted estimate-only deployment with a shared
/// service credential and hides every non-estimate application endpoint.
/// Full-platform deployments remain unchanged unless EstimateApi is enabled.
/// </summary>
public sealed class EstimateApiSecurityMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private static readonly PathString EstimatePath = "/api/v1/adjudication/estimate";
    private readonly RequestDelegate _next;

    public EstimateApiSecurityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IOptions<EstimateApiOptions> configured)
    {
        var options = configured.Value;
        if (!options.Enabled || IsHealthPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (options.IsEstimateOnlyEnabled && !context.Request.Path.Equals(EstimatePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var supplied = context.Request.Headers[ApiKeyHeader].FirstOrDefault();
        if (!FixedTimeEquals(supplied, options.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    private static bool IsHealthPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/ready") ||
        path.StartsWithSegments("/live");

    private static bool FixedTimeEquals(string? supplied, string expected)
    {
        if (supplied is null) return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
