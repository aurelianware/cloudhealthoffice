using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Entra;

/// <summary>
/// Validates the caller's Entra ID access token. The portal forwards the
/// signed-in user's token; nothing about the user is taken from anywhere else.
/// </summary>
public static class EntraAuthentication
{
    public static IServiceCollection AddEntraUserTokenValidation(
        this IServiceCollection services, IConfiguration configuration, TokenServiceOptions options)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration, "AzureAd");

        // Runs after Microsoft.Identity.Web's own configuration, so these rules
        // are the ones in force whatever its defaults are.
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
        {
            jwt.MapInboundClaims = false;

            var tvp = jwt.TokenValidationParameters;
            tvp.ValidateIssuer = true;
            tvp.IssuerValidator = (issuer, token, _) => ValidateIssuer(issuer, token, options);
            tvp.ValidateAudience = true;
            tvp.AudienceValidator = null;
            tvp.ValidAudience = null;
            tvp.ValidAudiences = options.Audiences.ToArray();
            tvp.ValidateLifetime = true;
            tvp.RequireExpirationTime = true;
            tvp.RequireSignedTokens = true;
            tvp.ValidateIssuerSigningKey = true;
            tvp.ClockSkew = TimeSpan.FromMinutes(2);
            tvp.NameClaimType = "name";
            tvp.RoleClaimType = "roles";

            var events = jwt.Events ??= new JwtBearerEvents();

            // The workload exchange presents a Kubernetes token, which that
            // endpoint validates itself. It is not an Entra token, so Entra
            // validation (and its refusal audit line) is skipped there.
            var innerReceived = events.OnMessageReceived;
            events.OnMessageReceived = async ctx =>
            {
                // Likewise the service-token exchange: an app-only workload-identity
                // token, validated by that endpoint against its own audience.
                if (ctx.Request.Path.Equals(Workload.WorkloadTokenEndpoint.Path, StringComparison.OrdinalIgnoreCase)
                    || ctx.Request.Path.Equals(ServiceTokens.ServiceTokenEndpoint.Path, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.NoResult();
                    return;
                }
                if (innerReceived != null)
                    await innerReceived(ctx);
            };

            var innerValidated = events.OnTokenValidated;
            events.OnTokenValidated = async ctx =>
            {
                if (innerValidated != null)
                    await innerValidated(ctx);
                if (ctx.Result?.Failure != null)
                    return;

                var failure = DelegatedUserTokenFailure(ctx.Principal!, options);
                if (failure != null)
                    ctx.Fail(failure);
            };

            var innerFailed = events.OnAuthenticationFailed;
            events.OnAuthenticationFailed = async ctx =>
            {
                if (innerFailed != null)
                    await innerFailed(ctx);
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("CloudHealthOffice.TokenService.Audit")
                    .LogWarning("Token refused: outcome={Outcome} reason={Reason}",
                        "invalid_token", ctx.Exception.GetType().Name);
            };

            events.OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            };
        });

        return services;
    }

    /// <summary>
    /// The token must be a delegated user token for CHO: it carries the
    /// required scope in <c>scp</c>, identifies its user by <c>tid</c> and
    /// <c>oid</c>, and is not an app-only token.
    /// </summary>
    internal static string? DelegatedUserTokenFailure(System.Security.Claims.ClaimsPrincipal principal, TokenServiceOptions options)
    {
        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
            return "app_only_token";

        var scp = principal.FindFirst("scp")?.Value;
        if (string.IsNullOrWhiteSpace(scp))
            return "no_delegated_scope";

        var scopes = scp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(options.RequiredScope, StringComparer.Ordinal))
            return "missing_scope";

        if (!Guid.TryParse(principal.FindFirst("tid")?.Value, out _)
            || string.IsNullOrWhiteSpace(principal.FindFirst("oid")?.Value))
            return "missing_tid_or_oid";

        return null;
    }

    /// <summary>
    /// Multi-tenant issuer check: the issuer must be one of the configured
    /// Entra forms instantiated with the token's own <c>tid</c>. A token from
    /// directory A can therefore never present itself as coming from B.
    /// </summary>
    internal static string ValidateIssuer(string issuer, SecurityToken token, TokenServiceOptions options)
    {
        var tid = token switch
        {
            JsonWebToken jwt when jwt.TryGetPayloadValue<string>("tid", out var t) => t,
            System.IdentityModel.Tokens.Jwt.JwtSecurityToken legacy => legacy.Payload.TryGetValue("tid", out var t) ? t as string : null,
            _ => null,
        };

        if (string.IsNullOrEmpty(tid) || !Guid.TryParse(tid, out _))
            throw new SecurityTokenInvalidIssuerException("The token has no valid tid claim.");

        foreach (var template in options.EffectiveIssuerTemplates)
        {
            if (string.Equals(issuer, template.Replace("{tid}", tid, StringComparison.Ordinal), StringComparison.Ordinal))
                return issuer;
        }

        throw new SecurityTokenInvalidIssuerException("The token issuer does not match its tid.");
    }
}

/// <summary>The Entra user behind a validated token.</summary>
public sealed record EntraUser(
    string Tid,
    string Oid,
    string? Username,
    string? Name,
    string? GivenName,
    string? FamilyName,
    IReadOnlyCollection<string> AppRoles)
{
    public static EntraUser From(System.Security.Claims.ClaimsPrincipal principal)
    {
        string? Get(string type) => principal.FindFirst(type)?.Value is { Length: > 0 } v ? v : null;
        return new EntraUser(
            Get("tid")!,
            Get("oid")!,
            Get("preferred_username") ?? Get("upn") ?? Get("email"),
            Get("name"),
            Get("given_name"),
            Get("family_name"),
            principal.FindAll("roles").Select(c => c.Value).ToArray());
    }
}
