using FhirService.Services.Identity;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Middleware;

/// <summary>
/// For SMART callers only: a trusted issuer confined to a list of tenants may
/// not authenticate outside it, however the token's tenant claim is shaped.
/// The tenant itself was already taken from the token by the shared
/// TenantMiddleware (see <see cref="SmartTenant"/> for how a SMART token's
/// tenant is established). CHO callers are governed by the CHO issuer trust
/// and pass straight through.
/// </summary>
public sealed class SmartIssuerTenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SmartIssuerTenantMiddleware> _logger;

    public SmartIssuerTenantMiddleware(RequestDelegate next, ILogger<SmartIssuerTenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (FhirCallerSchemes.IsSmart(context.User)
            && context.Items[AuthenticatedCaller.HttpContextItemKey] is AuthenticatedCaller caller
            && context.Items["TenantId"] is string tenantId
            && context.RequestServices.GetService<TrustedIssuerRegistry>()?.Resolve(caller.Issuer) is { } issuer
            && !TrustedIssuerRegistry.IssuerMayServeTenant(issuer, tenantId))
        {
            _logger.LogWarning(
                "Issuer {Issuer} is not permitted to authenticate tenant {TenantId}",
                Clean(caller.Issuer), Clean(tenantId));
            await FhirErrorResponse.WriteAsync(context, 403,
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Forbidden,
                "The authenticated issuer is not permitted to serve this tenant.");
            return;
        }

        await _next(context);
    }

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// Gives tenant refusals the FHIR shape. The shared TenantMiddleware (inside
/// UseChoAuthentication) is what enforces the rule: tenant from the token only,
/// 401 without one, 403 when X-Tenant-ID names another tenant. It answers in
/// CHO's JSON error shape, which a strict FHIR client cannot parse, so this
/// runs just ahead of it, applies the same rule and answers with an
/// OperationOutcome. Whatever passes here passes there unchanged.
/// </summary>
public sealed class FhirTenantRefusalMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CloudHealthOffice.Infrastructure.Middleware.TenantMiddlewareOptions _options;

    public FhirTenantRefusalMiddleware(
        RequestDelegate next, CloudHealthOffice.Infrastructure.Middleware.TenantMiddlewareOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true
            && !_options.PassthroughPaths.Any(p => context.Request.Path.StartsWithSegments(p)))
        {
            var tokenTenant = _options.TenantClaimTypes
                .Select(t => context.User.FindFirst(t)?.Value)
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            var headerTenant = context.Request.Headers[
                CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware.TenantHeaderName].FirstOrDefault();

            if (string.IsNullOrEmpty(tokenTenant))
            {
                await FhirErrorResponse.WriteAsync(context, 401,
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Login,
                    "The access token does not carry a tenant.");
                return;
            }

            if (!string.IsNullOrEmpty(headerTenant) && !string.Equals(headerTenant, tokenTenant, StringComparison.Ordinal))
            {
                await FhirErrorResponse.WriteAsync(context, 403,
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Forbidden,
                    "Tenant context conflict: the token and the request header name different tenants.");
                return;
            }
        }

        await _next(context);
    }
}

public static class TenantContextExtensions
{
    /// <summary>
    /// The authenticated tenant, set by the shared TenantMiddleware from the
    /// validated token (CHO or SMART). Null when the request has none.
    /// </summary>
    public static string? GetTenantId(this HttpContext context)
        => context.Items["TenantId"] as string;
}
