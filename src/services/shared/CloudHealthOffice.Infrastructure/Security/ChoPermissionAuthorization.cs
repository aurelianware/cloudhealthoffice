using Microsoft.AspNetCore.Authorization;
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
