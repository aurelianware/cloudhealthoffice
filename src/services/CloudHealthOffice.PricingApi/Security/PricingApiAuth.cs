using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CloudHealthOffice.PricingApi.Security;

/// <summary>
/// Two kinds of caller, two schemes:
/// <list type="bullet">
///   <item>CHO callers (the portal, other services) present a CHO token (the
///   shared "Bearer" scheme from <c>AddChoAuthentication</c>).</item>
///   <item>External Pricing API customers present their API key
///   (<see cref="PricingApiKeyAuthenticationHandler.SchemeName"/>).</item>
/// </list>
/// A policy scheme sends each request to exactly one of them: an
/// <c>X-API-Key</c> header selects the API-key scheme, anything else the CHO
/// scheme. Either way the tenant comes from the credential.
/// </summary>
public static class PricingApiAuth
{
    public const string CallerScheme = "PricingApiCaller";

    /// <summary>Admits an API-key customer, or a CHO caller holding <see cref="PricePermissions"/>.</summary>
    public const string CallerPolicy = "pricing-api:caller";

    /// <summary>
    /// CHO permissions (any of) for pricing reads and read-only repricing:
    /// claims staff, benefits readers and contract analysts.
    /// </summary>
    public const string PricePermissions = "contracts:read,claims:work,benefits:read";

    /// <summary>
    /// Every write this service has changes global data (the Medicare fee
    /// schedules every caller prices against) or the API keys of every
    /// external customer. Neither belongs to a CHO tenant.
    /// </summary>
    public const string GlobalWritePermission = "platform:admin";

    public static IServiceCollection AddPricingApiCallers(this IServiceCollection services)
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
            .AddPolicyScheme(CallerScheme, "CHO token or Pricing API key", policy =>
            {
                policy.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(PricingApiKeyAuthenticationHandler.ApiKeyHeader)
                        ? PricingApiKeyAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, PricingApiKeyAuthenticationHandler>(
                PricingApiKeyAuthenticationHandler.SchemeName, _ => { });

        services.AddSingleton<IAuthorizationHandler, PricingApiCallerHandler>();
        services.AddAuthorization(options => options.AddPolicy(CallerPolicy, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new PricingApiCallerRequirement())));

        return services;
    }

    /// <summary>
    /// True only for a principal the API-key handler built. A CHO token cannot
    /// claim this: the authentication type is set by the handler, not read
    /// from the token.
    /// </summary>
    public static bool IsApiKeyCustomer(ClaimsPrincipal principal) =>
        principal.Identities.Count() == 1 &&
        principal.Identity is { IsAuthenticated: true } identity &&
        identity.AuthenticationType == PricingApiKeyAuthenticationHandler.SchemeName;

    /// <summary>
    /// Fee schedules whose rates CMS publishes (Medicare PFS/RBRVS, OPPS
    /// Addendum B, MS-DRG Table 5). Only these are served to anonymous callers.
    /// Medicaid and commercial schedules are not, whoever loaded them.
    /// </summary>
    public static bool IsPublicCmsSchedule(FeeScheduleType type) =>
        type is FeeScheduleType.MedicareRbrvs or FeeScheduleType.MedicareOpps or FeeScheduleType.MedicareDrg;

    /// <summary>
    /// The rate-limit bucket for a request, from the authenticated caller only:
    /// an API-key customer by its key id, a CHO caller by tenant and subject,
    /// anyone else (anonymous, or an unknown or deactivated key) by client
    /// address. Headers are never read here, so a made-up key cannot open a
    /// fresh bucket.
    /// </summary>
    public static string RateLimitPartition(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var subject = user.FindFirst(ChoClaimTypes.Subject)?.Value;
            if (IsApiKeyCustomer(user) && !string.IsNullOrEmpty(subject))
                return "key:" + subject;

            var tenant = user.FindFirst(ChoClaimTypes.TenantId)?.Value;
            if (!string.IsNullOrEmpty(subject))
                return $"cho:{tenant}:{subject}";
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    public static int MonthlyLimit(PricingTier tier) => tier switch
    {
        PricingTier.Free => 1_000,
        PricingTier.Starter => 10_000,
        PricingTier.Professional => 100_000,
        PricingTier.Enterprise => int.MaxValue,
        _ => 1_000
    };
}

public sealed class PricingApiCallerRequirement : IAuthorizationRequirement;

public sealed class PricingApiCallerHandler : AuthorizationHandler<PricingApiCallerRequirement>
{
    private static readonly string[] Permissions =
        PricingApiAuth.PricePermissions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PricingApiCallerRequirement requirement)
    {
        if (PricingApiAuth.IsApiKeyCustomer(context.User) ||
            Permissions.Any(p => ChoPrincipal.HasPermission(context.User, p)))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Admits API-key customers and CHO callers holding <see cref="PricingApiAuth.PricePermissions"/>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PricingApiCallerAttribute : AuthorizeAttribute
{
    public PricingApiCallerAttribute() => Policy = PricingApiAuth.CallerPolicy;
}

/// <summary>
/// Monthly quota for API-key customers (counted in claim lines). Over the limit
/// the request is answered 429 before any pricing runs. CHO callers are not
/// metered.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PricingQuotaAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!PricingApiAuth.IsApiKeyCustomer(http.User) ||
            !http.Items.TryGetValue(PricingApiKeyAuthenticationHandler.ApiKeyRecordItem, out var item) ||
            item is not ApiKeyRecord record)
        {
            await next();
            return;
        }

        var limit = PricingApiAuth.MonthlyLimit(record.Tier);
        http.Response.Headers["X-RateLimit-Limit"] = limit.ToString();

        if (record.CurrentMonthUsage >= limit)
        {
            http.Response.Headers["X-RateLimit-Remaining"] = "0";
            context.Result = new ObjectResult(new ApiResponse<object>
            {
                Success = false,
                Error = new ApiError
                {
                    Code = "RATE_LIMIT_EXCEEDED",
                    Message = $"Monthly limit of {limit:N0} claims reached for your {record.Tier} plan. Upgrade at https://cloudhealthoffice.com/pricing-api/upgrade"
                }
            })
            { StatusCode = StatusCodes.Status429TooManyRequests };
            return;
        }

        http.Response.Headers["X-RateLimit-Remaining"] = (limit - record.CurrentMonthUsage).ToString();
        await next();
    }
}
