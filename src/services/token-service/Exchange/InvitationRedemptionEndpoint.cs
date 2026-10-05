using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CloudHealthOffice.TokenService.Directory;
using CloudHealthOffice.TokenService.Entra;
using Microsoft.AspNetCore.RateLimiting;

namespace CloudHealthOffice.TokenService.Exchange;

/// <summary>
/// <c>POST /v1/invitations/redeem</c>: the signed-in Entra user presents an
/// invitation code. tenant-service links the token's tid+oid to the invited
/// user (only if the signed-in username is the invited address), and the user
/// then gets a CHO token for that tenant through the normal exchange.
///
/// The Entra token is validated exactly as for <c>/v1/token/exchange</c>.
/// Attempts are limited per Entra identity and every attempt is audited,
/// without the code or any email address.
/// </summary>
public static class InvitationRedemptionEndpoint
{
    public const string Path = "/v1/invitations/redeem";
    public const string RateLimitPolicy = "invitation-redeem";

    /// <summary>The refusals the caller sees, and their HTTP status.</summary>
    internal static int StatusFor(string error) => error switch
    {
        "not_found" or "expired" or "revoked" => StatusCodes.Status400BadRequest,
        "email_mismatch" => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status409Conflict, // already_redeemed, identity_in_use, conflict
    };

    public static IServiceCollection AddInvitationRedemptionRateLimit(this IServiceCollection services, TokenServiceOptions options)
        => services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(http),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.InvitationRedeemPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            limiter.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                EntraUser? user = http.User.Identity?.IsAuthenticated == true ? EntraUser.From(http.User) : null;
                http.RequestServices.GetRequiredService<TokenAudit>()
                    .InvitationRedemption(user, "refused", null, null, "rate_limited");
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.Response.WriteAsJsonAsync(new { error = "rate_limited" }, ct);
            };
        });

    /// <summary>
    /// One bucket per Entra identity. Authorization runs first, so every request
    /// that reaches the limiter carries a validated tid and oid.
    /// </summary>
    private static string PartitionKey(HttpContext http)
    {
        var tid = http.User.FindFirst("tid")?.Value;
        var oid = http.User.FindFirst("oid")?.Value;
        return string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid) ? "unidentified" : $"{tid}|{oid}".ToLowerInvariant();
    }

    public static RouteHandlerBuilder MapInvitationRedemption(this IEndpointRouteBuilder app)
        => app.MapPost(Path, HandleAsync).RequireRateLimiting(RateLimitPolicy);

    internal static async Task<IResult> HandleAsync(
        HttpContext http, ITenantDirectory directory, TokenExchangeService exchange, TokenAudit audit)
    {
        var user = EntraUser.From(http.User);

        string? code = null;
        try
        {
            var body = await http.Request.ReadFromJsonAsync<RedeemRequest>(http.RequestAborted);
            code = body?.Code?.Trim();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // handled below
        }
        if (string.IsNullOrEmpty(code) || code.Length > 256)
        {
            audit.InvitationRedemption(user, "refused", null, null, "invalid_request");
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }

        InvitationRedemption redemption;
        try
        {
            redemption = await directory.RedeemInvitationAsync(code, user.Tid, user.Oid, user.Username ?? string.Empty, http.RequestAborted);
        }
        catch (TenantDirectoryUnavailableException)
        {
            audit.InvitationRedemption(user, "refused", null, null, "tenant_service_unavailable");
            return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!redemption.Succeeded)
        {
            var error = redemption.Error ?? "conflict";
            audit.InvitationRedemption(user, "refused", null, null, error);
            object payload = error == "email_mismatch"
                ? new { error, invitedEmail = redemption.InvitedEmail }
                : new { error };
            return Results.Json(payload, statusCode: StatusFor(error));
        }

        audit.InvitationRedemption(user, "redeemed", redemption.TenantId, redemption.UserId, "ok");

        // The identity is now linked: issue the token through the normal rules,
        // which re-check the user, its status and the tenant.
        try
        {
            var outcome = await exchange.ExchangeAsync(user, redemption.TenantId, http.RequestAborted);
            if (outcome.Response == null)
                return Results.Json(new { error = "no_access" }, statusCode: StatusCodes.Status403Forbidden);

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            return Results.Json(outcome.Response);
        }
        catch (TenantDirectoryUnavailableException)
        {
            audit.Unavailable(user, redemption.TenantId, "tenant_service_unavailable");
            return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SigningUnavailableException ex)
        {
            audit.Unavailable(user, redemption.TenantId, "signing_unavailable:" + ex.InnerException?.GetType().Name);
            return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal sealed record RedeemRequest([property: JsonPropertyName("code")] string? Code);
}
