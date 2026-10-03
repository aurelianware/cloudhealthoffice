using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TenantService.Security;

/// <summary>Permissions tenant-service enforces.</summary>
public static class TenantPermissions
{
    /// <summary>Cross-tenant administration: create/list/activate/suspend/delete tenants, tiers, the global role catalogue.</summary>
    public const string PlatformTenants = "platform:tenants";

    public const string UsersManage = "users:manage";
    public const string RolesManage = "roles:manage";
    public const string SettingsManage = "settings:manage";
    public const string OperatingModeManage = "operating-mode:manage";

    /// <summary>
    /// Policy for reads any authenticated user or service of the tenant may
    /// make (its own tenant record, operating mode, usage). The route tenant
    /// check (<see cref="RouteTenantFilter"/>) still applies.
    /// </summary>
    public const string MemberPolicy = "tenant:member";

    /// <summary>Roles a tenant administrator may not hand out: they act across tenants or impersonate a service.</summary>
    public static readonly IReadOnlySet<string> PlatformOnlyRoles =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ChoRolePermissions.PlatformAdmin, ChoServiceRole.Name };
}

/// <summary>
/// Exempts an endpoint from <see cref="RouteTenantFilter"/>. Only for endpoints
/// whose caller is authorized on its identity rather than its tenant
/// (<see cref="RequireServiceClientAttribute"/>), such as token-service's
/// cross-tenant identity lookups.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipRouteTenantCheckAttribute : Attribute
{
}

/// <summary>
/// Audit trail for platform and cross-tenant actions, on its own log category
/// so it can be routed and retained separately.
/// </summary>
public sealed class TenantAuditLog
{
    public const string Category = "CloudHealthOffice.TenantService.Audit";

    private readonly ILogger _logger;
    private readonly ICurrentActor _actor;

    public TenantAuditLog(ILoggerFactory loggerFactory, ICurrentActor actor)
    {
        _logger = loggerFactory.CreateLogger(Category);
        _actor = actor;
    }

    /// <summary>Records who did what to which tenant.</summary>
    public void Record(string action, string? targetTenant, string outcome = "allowed")
    {
        _logger.LogWarning(
            "Tenant audit: {Action} on tenant {TargetTenant} by {Actor} (token tenant {ActorTenant}, service {IsService}): {Outcome}",
            Sanitize(action), Sanitize(targetTenant), Sanitize(SafeActor()), Sanitize(SafeTenant()),
            _actor.IsAuthenticated && _actor.IsService, outcome);
    }

    private string SafeActor()
    {
        try { return _actor.UserId; }
        catch (UnauthorizedAccessException) { return "anonymous"; }
    }

    private string SafeTenant()
    {
        try { return _actor.TenantId; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    internal static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// Every route with a <c>{tenantId}</c> segment acts on that tenant only when
/// it is the caller's own tenant (from the token). A caller holding
/// <c>platform:tenants</c> may act on any tenant, and every such cross-tenant
/// action is written to the audit log. Anyone else gets 403 before the action
/// runs. Service tokens never hold <c>platform:tenants</c>, so a service can
/// only reach the tenant its token names.
/// </summary>
public sealed class RouteTenantFilter : IAsyncActionFilter
{
    public const string RouteKey = "tenantId";

    private readonly ICurrentActor _actor;
    private readonly TenantAuditLog _audit;
    private readonly ILogger<RouteTenantFilter> _logger;

    public RouteTenantFilter(ICurrentActor actor, TenantAuditLog audit, ILogger<RouteTenantFilter> logger)
    {
        _actor = actor;
        _audit = audit;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<SkipRouteTenantCheckAttribute>().Any()
            || !context.RouteData.Values.TryGetValue(RouteKey, out var value)
            || value is not string routeTenant)
        {
            await next();
            return;
        }

        if (!_actor.IsAuthenticated)
        {
            // Only [AllowAnonymous] endpoints get here without a caller; none
            // of them may name a tenant.
            context.Result = new UnauthorizedResult();
            return;
        }

        var tokenTenant = context.HttpContext.Items["TenantId"] as string;
        if (string.Equals(routeTenant, tokenTenant, StringComparison.Ordinal))
        {
            await next();
            return;
        }

        var action = $"{context.HttpContext.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template ?? context.HttpContext.Request.Path}";
        if (_actor.HasPermission(TenantPermissions.PlatformTenants))
        {
            _audit.Record("cross-tenant " + action, routeTenant);
            await next();
            return;
        }

        _logger.LogWarning(
            "Tenant route refused: path tenant {PathTenant} does not match token tenant {TokenTenant} (subject {Subject}, {Action})",
            TenantAuditLog.Sanitize(routeTenant), TenantAuditLog.Sanitize(tokenTenant),
            TenantAuditLog.Sanitize(_actor.UserId), TenantAuditLog.Sanitize(action));
        context.Result = new ObjectResult(new { error = "tenant_mismatch", message = "The tenant in the path does not match the authenticated tenant." })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
