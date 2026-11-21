using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FhirService.Services.Identity;

/// <summary>
/// Declares which callers an endpoint serves and what each must hold.
///
/// <list type="bullet">
///   <item><c>Smart = true</c>: a SMART caller is admitted here. What it may
///   then see is decided by its scopes and patient binding
///   (SmartScopeEnforcementMiddleware), Provider Access attribution/consent
///   (ProviderAccessAuthorizationFilter) and the controller's own patient
///   checks. A SMART token never satisfies a CHO permission.</item>
///   <item><c>Cho = "x:read"</c>: a CHO caller is admitted with that permission
///   (comma-separated means any of). A CHO caller has no SMART scopes and no
///   patient binding; its permission and its tenant govern it. A CHO token
///   never satisfies a SMART check.</item>
/// </list>
///
/// An endpoint with neither is closed to both. An unannotated action is denied
/// by the shared default-permission convention (fhir-service configures no
/// default), so a new endpoint is closed until someone decides who may call it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class FhirAccessAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "fhir-access:";

    public FhirAccessAttribute(bool smart, string? cho)
        : this(smart, cho, cdsHooks: false)
    {
    }

    /// <param name="cdsHooks">An EHR authenticated by its CDS Hooks client JWT is admitted (CDS Hooks endpoints only).</param>
    public FhirAccessAttribute(bool smart, string? cho, bool cdsHooks)
    {
        Smart = smart;
        Cho = cho;
        CdsHooks = cdsHooks;
        var callers = string.Join('+', new[] { smart ? "smart" : null, cdsHooks ? "cds-hooks" : null }.OfType<string>());
        Policy = $"{PolicyPrefix}{(callers.Length > 0 ? callers : "-")}|{cho ?? "-"}";
    }

    public bool Smart { get; }

    public bool CdsHooks { get; }

    public string? Cho { get; }
}

public sealed class FhirAccessRequirement : IAuthorizationRequirement
{
    public FhirAccessRequirement(bool smart, IReadOnlyList<string> choAnyOf, bool cdsHooks = false)
    {
        Smart = smart;
        ChoAnyOf = choAnyOf;
        CdsHooks = cdsHooks;
    }

    public bool Smart { get; }

    public bool CdsHooks { get; }

    public IReadOnlyList<string> ChoAnyOf { get; }
}

public sealed class FhirAccessAuthorizationHandler : AuthorizationHandler<FhirAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FhirAccessRequirement requirement)
    {
        var user = context.User;

        if (requirement.Smart && FhirCallerSchemes.IsSmart(user))
            context.Succeed(requirement);
        else if (requirement.CdsHooks && FhirCallerSchemes.IsCdsHooksClient(user))
            context.Succeed(requirement);
        else if (requirement.ChoAnyOf.Count > 0
                 && FhirCallerSchemes.IsCho(user)
                 && requirement.ChoAnyOf.Any(p => ChoPrincipal.HasPermission(user, p)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds <c>fhir-access:</c> policies and defers everything else to the
/// shared provider (perm:, svc:, the deny policy, named policies).
/// </summary>
public sealed class FhirAccessPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly PermissionPolicyProvider _shared;

    public FhirAccessPolicyProvider(IOptions<AuthorizationOptions> options)
        => _shared = new PermissionPolicyProvider(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _shared.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _shared.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(FhirAccessAttribute.PolicyPrefix, StringComparison.Ordinal))
            return _shared.GetPolicyAsync(policyName);

        var parts = policyName[FhirAccessAttribute.PolicyPrefix.Length..].Split('|');
        var callers = parts[0].Split('+');
        var smart = callers.Contains("smart");
        var cdsHooks = callers.Contains("cds-hooks");
        var cho = parts.Length > 1 && parts[1] != "-"
            ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new FhirAccessRequirement(smart, cho, cdsHooks))
            .Build());
    }
}

public static class FhirAccessServiceCollectionExtensions
{
    /// <summary>Registers the fhir-access policies. Call after AddChoAuthentication.</summary>
    public static IServiceCollection AddFhirAccessPolicies(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, FhirAccessPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, FhirAccessAuthorizationHandler>();
        return services;
    }
}
