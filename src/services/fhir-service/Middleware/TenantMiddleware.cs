using FhirService.Services.Identity;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Middleware;

/// <summary>
/// Establishes the tenant a FHIR request acts within.
/// Passes FHIR metadata and health endpoints without requiring a tenant.
///
/// TENANT IS AUTHORITY, so where it comes from is a security decision, not a
/// plumbing one. The order below is a precedence, and the first source that
/// speaks wins permanently:
///
///   1. A tenant claim from the token, when the trusted issuer maps one. An
///      issuer CHO trusts saying "this caller belongs to tenant X" is the only
///      statement of tenancy that is itself authenticated.
///   2. The X-Tenant-ID header, for service-to-service calls inside the mesh.
///
/// The rule that makes (2) safe is that it may only fill a VACUUM. Previously
/// the header was consulted whenever the token carried no tenant claim, which
/// meant any authenticated caller whose issuer did not map a tenant could name
/// any tenant they liked and be believed. Now, when the token asserts a tenant,
/// a header that disagrees is a rejected request rather than an override — the
/// two can never silently diverge — and where the trusted issuer is scoped to a
/// set of tenants, the resolved tenant must be one of them, so customer A's IdP
/// cannot authenticate into customer B however its claims are shaped.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public TenantMiddleware(
        RequestDelegate next, ILogger<TenantMiddleware> logger, IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPassthroughPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var caller = context.Items[AuthenticatedCaller.HttpContextItemKey] as AuthenticatedCaller;
        var resolution = ResolveTenant(context, caller, _environment.IsDevelopment());

        if (resolution.Conflict)
        {
            // A header naming a different tenant than the token is not a
            // preference to resolve; it is a request whose own two statements
            // of authority disagree.
            _logger.LogWarning(
                "FHIR request tenant conflict: token asserts {TokenTenant}, header asserts {HeaderTenant}",
                SanitizeForLog(resolution.TokenTenant), SanitizeForLog(resolution.HeaderTenant));
            await FhirErrorResponse.WriteAsync(context, 403,
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Forbidden,
                "Tenant context conflict: the token and the request header name different tenants.");
            return;
        }

        var tenantId = resolution.TenantId;

        if (string.IsNullOrEmpty(tenantId))
        {
            _logger.LogWarning("FHIR request missing tenant context: {Path}", SanitizeForLog(context.Request.Path));
            await FhirErrorResponse.WriteAsync(context, 401,
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Login,
                "Missing tenant context.");
            return;
        }

        // An issuer confined to a tenant list may not authenticate outside it,
        // however the tenant was resolved.
        var registry = context.RequestServices.GetService<TrustedIssuerRegistry>();
        if (caller != null && registry != null)
        {
            var issuer = registry.Resolve(caller.Issuer);
            if (issuer != null && !TrustedIssuerRegistry.IssuerMayServeTenant(issuer, tenantId))
            {
                _logger.LogWarning(
                    "Issuer {Issuer} is not permitted to authenticate tenant {TenantId}",
                    SanitizeForLog(caller.Issuer), SanitizeForLog(tenantId));
                await FhirErrorResponse.WriteAsync(context, 403,
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Forbidden,
                    "The authenticated issuer is not permitted to serve this tenant.");
                return;
            }
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
        || path.StartsWithSegments("/fhir/r4/adapter-status")
        || path.StartsWithSegments("/fhir/r4/.well-known")
        || path.StartsWithSegments("/fhir/r4/StructureDefinition")
        || path.StartsWithSegments("/fhir/r4/OperationDefinition")
        || path.StartsWithSegments("/fhir/r4/CodeSystem")
        || path.StartsWithSegments("/fhir/r4/ValueSet")
        || path.StartsWithSegments("/swagger");

    internal readonly record struct TenantResolution(
        string? TenantId, string? TokenTenant, string? HeaderTenant, bool Conflict);

    /// <summary>
    /// Resolves the tenant and reports whether the request's own sources of
    /// authority contradict each other.
    /// </summary>
    internal static TenantResolution ResolveTenant(
        HttpContext context, AuthenticatedCaller? caller, bool isDevelopmentHost)
    {
        // The trusted issuer's mapped claim first, then the conventional claim
        // names, so a deployment whose issuer maps a differently named claim is
        // still authenticated by its token rather than by a header.
        var tokenTenant = caller?.TenantClaim
            ?? context.User?.FindFirst("tenant_id")?.Value
            ?? context.User?.FindFirst("extension_TenantId")?.Value;

        var headerTenant = context.Request.Headers.TryGetValue("X-Tenant-ID", out var header)
            ? header.FirstOrDefault()
            : null;

        if (!string.IsNullOrEmpty(tokenTenant))
        {
            // The header may echo the token but never contradict it.
            if (!string.IsNullOrEmpty(headerTenant) &&
                !string.Equals(headerTenant, tokenTenant, StringComparison.Ordinal))
            {
                return new TenantResolution(null, tokenTenant, headerTenant, Conflict: true);
            }

            return new TenantResolution(tokenTenant, tokenTenant, headerTenant, Conflict: false);
        }

        if (!string.IsNullOrEmpty(headerTenant))
            return new TenantResolution(headerTenant, null, headerTenant, Conflict: false);

        // Development convenience only. Honouring this on a production host
        // would be an unauthenticated tenant selector.
        if (isDevelopmentHost &&
            context.Request.Headers.TryGetValue("X-Dev-Tenant-ID", out var devHeader))
        {
            return new TenantResolution(devHeader.FirstOrDefault(), null, null, Conflict: false);
        }

        return new TenantResolution(null, null, null, Conflict: false);
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
