namespace FhirService.Middleware;

/// <summary>
/// Extracts tenant context from JWT claim or service-to-service headers.
/// Passes FHIR metadata and health endpoints without requiring a tenant.
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
        if (IsPassthroughPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var tenantId = ExtractTenantId(context);

        if (string.IsNullOrEmpty(tenantId))
        {
            _logger.LogWarning("FHIR request missing tenant context: {Path}", SanitizeForLog(context.Request.Path));
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Missing tenant context" });
            return;
        }

        context.Items["TenantId"] = tenantId;
        _logger.LogInformation("FHIR request from tenant: {TenantId}", SanitizeForLog(tenantId));

        await _next(context);
    }

    // Conformance-discovery endpoints bypass tenant context for the same
    // reason as /fhir/r4/metadata: clients need to read them anonymously
    // before they have a tenant binding.
    private static bool IsPassthroughPath(PathString path)
        => path.StartsWithSegments("/health")
        || path.StartsWithSegments("/ready")
        || path.StartsWithSegments("/live")
        || path.StartsWithSegments("/fhir/r4/metadata")
        || path.StartsWithSegments("/fhir/r4/.well-known")
        || path.StartsWithSegments("/fhir/r4/StructureDefinition")
        || path.StartsWithSegments("/fhir/r4/OperationDefinition")
        || path.StartsWithSegments("/fhir/r4/CodeSystem")
        || path.StartsWithSegments("/fhir/r4/ValueSet")
        || path.StartsWithSegments("/swagger");

    private static string? ExtractTenantId(HttpContext context)
    {
        // 1. JWT claim (preferred for user requests)
        var claim = context.User?.FindFirst("tenant_id")
                 ?? context.User?.FindFirst("extension_TenantId");
        if (claim != null) return claim.Value;

        // 2. Service-to-service header
        if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var header))
            return header.FirstOrDefault();

        // 3. Dev mode
        if (context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var devHeader))
            return devHeader.FirstOrDefault();

        return null;
    }

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty
           : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

public static class TenantMiddlewareExtensions
{
    public static string? GetTenantId(this HttpContext context)
        => context.Items["TenantId"] as string;
}
