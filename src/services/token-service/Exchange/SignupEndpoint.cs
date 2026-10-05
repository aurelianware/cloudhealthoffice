using System.Text.Json;
using System.Threading.RateLimiting;
using CloudHealthOffice.TokenService.Directory;
using CloudHealthOffice.TokenService.Entra;
using Microsoft.AspNetCore.RateLimiting;

namespace CloudHealthOffice.TokenService.Exchange;

/// <summary>
/// <c>POST /v1/signup</c>: self-service signup from the portal's Signup page.
/// The signed-in user has an Entra token but no CHO tenant yet, so no CHO token
/// exists to call tenant-service with; this endpoint validates the Entra token
/// (exactly as for the exchange) and asks tenant-service, over token-service's
/// own service token, to create a Trial subscription for the token's directory
/// (<c>tid</c>) with the token's username as admin. The body can choose only the
/// organization name, a self-service tier and the Stripe customer/subscription
/// ids; the directory, status, demo flag and admin list never come from it.
/// Attempts are limited per Entra identity and audited.
/// </summary>
public static class SignupEndpoint
{
    public const string Path = "/v1/signup";
    public const string RateLimitPolicy = "signup";

    public static IServiceCollection AddSignupRateLimit(this IServiceCollection services, TokenServiceOptions options)
        => services.Configure<RateLimiterOptions>(limiter => limiter.AddPolicy(RateLimitPolicy, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.InvitationRedeemPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })));

    private static string PartitionKey(HttpContext http)
    {
        var tid = http.User.FindFirst("tid")?.Value;
        var oid = http.User.FindFirst("oid")?.Value;
        return string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid) ? "unidentified" : $"{tid}|{oid}".ToLowerInvariant();
    }

    public static RouteHandlerBuilder MapSignup(this IEndpointRouteBuilder app)
        => app.MapPost(Path, HandleAsync).RequireRateLimiting(RateLimitPolicy);

    internal static async Task<IResult> HandleAsync(HttpContext http, ITenantDirectory directory, ILoggerFactory loggers)
    {
        var audit = loggers.CreateLogger("CloudHealthOffice.TokenService.Audit");
        var user = EntraUser.From(http.User);
        var email = user.Username;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            audit.LogWarning("CHO signup refused: tid={Tid} oid={Oid} reason=no_username", user.Tid, user.Oid);
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }

        SignupForm? form = null;
        try
        {
            form = await http.Request.ReadFromJsonAsync<SignupForm>(http.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // handled below
        }
        if (form is null || string.IsNullOrWhiteSpace(form.OrganizationName) || string.IsNullOrWhiteSpace(form.Tier))
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);

        SignupOutcome outcome;
        try
        {
            outcome = await directory.SignupAsync(form, user.Tid, user.Oid, email, http.RequestAborted);
        }
        catch (TenantDirectoryUnavailableException)
        {
            audit.LogError("CHO signup refused: tid={Tid} oid={Oid} reason=tenant_service_unavailable", user.Tid, user.Oid);
            return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!outcome.Succeeded)
        {
            var error = outcome.Error ?? "invalid_request";
            audit.LogWarning("CHO signup refused: tid={Tid} oid={Oid} reason={Reason}", user.Tid, user.Oid, error);
            return Results.Json(new { error },
                statusCode: error == "already_subscribed" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
        }

        audit.LogInformation("CHO signup created: tid={Tid} oid={Oid} tenant={Tenant}", user.Tid, user.Oid, outcome.TenantId);
        return Results.Json(new { tenantId = outcome.TenantId }, statusCode: StatusCodes.Status201Created);
    }
}
