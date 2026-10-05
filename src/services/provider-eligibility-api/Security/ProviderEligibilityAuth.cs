using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace ProviderEligibilityApi.Security;

/// <summary>
/// Two kinds of caller, two schemes:
/// <list type="bullet">
///   <item>CHO callers present a CHO token (the shared "Bearer" scheme from
///   <c>AddChoAuthentication</c>) and need <see cref="CheckPermission"/>.</item>
///   <item>Provider applications present their API key
///   (<see cref="ProviderApiKeyAuthenticationHandler.SchemeName"/>).</item>
/// </list>
/// A policy scheme sends each request to exactly one of them: an
/// <c>X-Api-Key</c> header selects the API-key scheme, anything else the CHO
/// scheme. Either way the tenant comes from the credential and the shared
/// tenant middleware establishes it.
/// </summary>
public static class ProviderEligibilityAuth
{
    public const string CallerScheme = "ProviderEligibilityCaller";

    /// <summary>Admits a provider API client, or a CHO caller holding <see cref="CheckPermission"/>.</summary>
    public const string CallerPolicy = "provider-eligibility:caller";

    /// <summary>
    /// The CHO permission for running an outbound eligibility check (a
    /// read-only 270 inquiry) and searching the payer directory it routes by.
    /// </summary>
    public const string CheckPermission = "eligibility:check";

    public static IServiceCollection AddProviderEligibilityCallers(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                // AddChoAuthentication made "Bearer" the default; the selector
                // below still ends there for every request without an API key.
                options.DefaultScheme = CallerScheme;
                options.DefaultAuthenticateScheme = CallerScheme;
                options.DefaultChallengeScheme = CallerScheme;
                options.DefaultForbidScheme = CallerScheme;
            })
            .AddPolicyScheme(CallerScheme, "CHO token or provider API key", policy =>
            {
                policy.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ProviderApiKeyAuthenticationHandler.ApiKeyHeader)
                        ? ProviderApiKeyAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, ProviderApiKeyAuthenticationHandler>(
                ProviderApiKeyAuthenticationHandler.SchemeName, _ => { });

        services.AddSingleton<IAuthorizationHandler, ProviderEligibilityCallerHandler>();
        services.AddAuthorization(options => options.AddPolicy(CallerPolicy, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new ProviderEligibilityCallerRequirement())));

        return services;
    }

    /// <summary>
    /// True only for a principal the API-key handler built. A CHO token cannot
    /// claim this: the authentication type is set by the handler, not read
    /// from the token.
    /// </summary>
    public static bool IsProviderApiClient(ClaimsPrincipal principal) =>
        principal.Identities.Count() == 1 &&
        principal.Identity is { IsAuthenticated: true } identity &&
        identity.AuthenticationType == ProviderApiKeyAuthenticationHandler.SchemeName;

    /// <summary>Label for logs: the provider client name, or the CHO token subject.</summary>
    public static string CallerLabel(ClaimsPrincipal principal) =>
        IsProviderApiClient(principal)
            ? principal.FindFirst(ProviderApiKeyAuthenticationHandler.ClientClaim)?.Value ?? "provider-api"
            : "cho:" + (principal.FindFirst(ChoClaimTypes.Subject)?.Value ?? "unknown");
}

public sealed class ProviderEligibilityCallerRequirement : IAuthorizationRequirement;

public sealed class ProviderEligibilityCallerHandler : AuthorizationHandler<ProviderEligibilityCallerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProviderEligibilityCallerRequirement requirement)
    {
        if (ProviderEligibilityAuth.IsProviderApiClient(context.User) ||
            ChoPrincipal.HasPermission(context.User, ProviderEligibilityAuth.CheckPermission))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Admits provider API clients and CHO callers holding <c>eligibility:check</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ProviderEligibilityCallerAttribute : AuthorizeAttribute
{
    public ProviderEligibilityCallerAttribute() => Policy = ProviderEligibilityAuth.CallerPolicy;
}
