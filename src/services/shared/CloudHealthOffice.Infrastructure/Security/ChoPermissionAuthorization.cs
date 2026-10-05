using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Security;

/// <summary>
/// Requires the caller to hold a permission (e.g. <c>claims:adjust</c>).
/// Several permissions separated by commas mean "any of".
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequirePermissionAttribute(string permission)
    {
        Permission = permission;
        Policy = PolicyPrefix + permission;
    }

    public string Permission { get; }
}

/// <summary>
/// Restricts an endpoint to named service identities: a service token (the
/// <c>cho.service</c> role from an issuer trusted to mint it) whose
/// <c>sub</c> and <c>azp</c> both equal one of the client ids. A client id
/// starting with <c>wf-</c> names a Kubernetes workload instead, and is
/// matched only by a workload token (<c>cho.workload</c> from an issuer with
/// <see cref="ChoTrustedIssuer.AllowWorkloadIdentity"/>, i.e. token-service's
/// workload exchange), never by a service token: see
/// <see cref="ChoPrincipal.ServiceClientId"/>. User tokens,
/// whatever their permissions, and every other service are refused (403).
/// The token's tenant plays no part, so a service can call it with a
/// cross-tenant scope. Several client ids separated by commas mean "any of".
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireServiceClientAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "svc:";

    public RequireServiceClientAttribute(string clientId)
    {
        ClientId = clientId;
        Policy = PolicyPrefix + clientId;
    }

    public string ClientId { get; }
}

public sealed class ServiceClientRequirement : IAuthorizationRequirement
{
    public ServiceClientRequirement(IReadOnlyList<string> anyOf) => AnyOf = anyOf;

    public IReadOnlyList<string> AnyOf { get; }
}

public sealed class ServiceClientAuthorizationHandler : AuthorizationHandler<ServiceClientRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ServiceClientRequirement requirement)
    {
        var client = ChoPrincipal.ServiceClientId(context.User);
        if (client != null && requirement.AnyOf.Contains(client, StringComparer.Ordinal))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(IReadOnlyList<string> anyOf) => AnyOf = anyOf;

    public IReadOnlyList<string> AnyOf { get; }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (requirement.AnyOf.Any(p => ChoPrincipal.HasPermission(context.User, p)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds <c>perm:*</c> policies on demand, so a controller can name any
/// permission without a registration step.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>A policy nobody satisfies: the default for an unmapped endpoint.</summary>
    public const string DenyPolicy = "cho:deny";

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        => _fallback = new DefaultAuthorizationPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName == DenyPolicy)
        {
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(_ => false)
                .Build());
        }

        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var permissions = policyName[RequirePermissionAttribute.PolicyPrefix.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permissions))
                .Build());
        }

        if (policyName.StartsWith(RequireServiceClientAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var clients = policyName[RequireServiceClientAttribute.PolicyPrefix.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ServiceClientRequirement(clients))
                .Build());
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}

/// <summary>
/// Default-deny for controller actions. Every action that names neither a
/// permission nor <c>[AllowAnonymous]</c> receives the service's default read
/// permission (GET/HEAD) or write permission (everything else). A service that
/// configures no default gets <see cref="PermissionPolicyProvider.DenyPolicy"/>
/// on every unannotated action, so a new endpoint is closed until someone
/// decides who may call it.
/// </summary>
public sealed class DefaultPermissionConvention : IApplicationModelConvention
{
    private readonly string? _read;
    private readonly string? _write;

    public DefaultPermissionConvention(string? defaultReadPermission, string? defaultWritePermission)
    {
        _read = defaultReadPermission;
        _write = defaultWritePermission;
    }

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            var controllerDeclares = controller.Attributes.Any(IsAuthorizationDeclaration);

            foreach (var action in controller.Actions)
            {
                if (controllerDeclares || action.Attributes.Any(IsAuthorizationDeclaration))
                    continue;

                var isRead = action.Attributes
                    .OfType<Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider>()
                    .SelectMany(a => a.HttpMethods)
                    .DefaultIfEmpty("GET")
                    .All(m => m is "GET" or "HEAD");

                var permission = isRead ? _read : _write;
                var policy = permission is null
                    ? PermissionPolicyProvider.DenyPolicy
                    : RequirePermissionAttribute.PolicyPrefix + permission;

                foreach (var selector in action.Selectors)
                    selector.EndpointMetadata.Add(new AuthorizeAttribute(policy));
            }
        }
    }

    /// <summary>
    /// A permission or anonymous marker counts as a decision. A bare
    /// <c>[Authorize]</c> (authenticated only) does not: it would let any role
    /// call the endpoint, so the default permission still applies.
    /// </summary>
    private static bool IsAuthorizationDeclaration(object attribute)
        => attribute is RequirePermissionAttribute
           || attribute is IAllowAnonymous
           || (attribute is AuthorizeAttribute a && !string.IsNullOrEmpty(a.Policy));
}

/// <summary>
/// The fallback policy of a CHO service: what applies to any request whose
/// endpoint carries no authorization metadata of its own (no permission, no
/// policy, no <c>[AllowAnonymous]</c>). In practice that is a minimal-API
/// endpoint (<c>MapGet</c>, <c>MapPost</c>, ...) nobody annotated, so it is
/// held to the same default as an unannotated controller action
/// (<see cref="DefaultPermissionConvention"/>): the default read permission
/// for GET/HEAD, the default write permission for everything else, and
/// denied when that default is not configured.
///
/// Requests with no endpoint at all (health/metrics middleware, an unmatched
/// route) are not endpoints to protect: the passthrough paths need no caller,
/// and anything else needs an authenticated caller and then 404s.
/// </summary>
public sealed class DefaultPermissionFallbackRequirement : IAuthorizationRequirement
{
    public DefaultPermissionFallbackRequirement(
        string? defaultReadPermission, string? defaultWritePermission, IReadOnlyList<string> passthroughPaths)
    {
        DefaultReadPermission = defaultReadPermission;
        DefaultWritePermission = defaultWritePermission;
        PassthroughPaths = passthroughPaths;
    }

    public string? DefaultReadPermission { get; }

    public string? DefaultWritePermission { get; }

    public IReadOnlyList<string> PassthroughPaths { get; }

    /// <summary>The permission an unannotated endpoint answering <paramref name="method"/> requires, or null (denied).</summary>
    public string? PermissionFor(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? DefaultReadPermission : DefaultWritePermission;
}

public sealed class DefaultPermissionFallbackHandler : AuthorizationHandler<DefaultPermissionFallbackRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DefaultPermissionFallbackRequirement requirement)
    {
        // The authorization middleware passes the HttpContext. Anything else
        // (a direct IAuthorizationService call with no endpoint to judge) is denied.
        if (context.Resource is not HttpContext http)
            return Task.CompletedTask;

        if (requirement.PassthroughPaths.Any(p => http.Request.Path.StartsWithSegments(p)))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        if (http.GetEndpoint() is null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var permission = requirement.PermissionFor(http.Request.Method);
        if (permission != null && ChoPrincipal.HasPermission(context.User, permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
