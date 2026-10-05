using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SmartAuthService.Services;

/// <summary>
/// <c>SmartAuth:ExternalLogin</c>: the production member/provider sign-in, an
/// OpenID Connect relying party of one external identity provider (Microsoft
/// Entra External ID). See docs/security/smart-auth-tenancy.md.
/// </summary>
public sealed class ExternalLoginOptions
{
    public const string SectionName = "SmartAuth:ExternalLogin";

    /// <summary>The authentication scheme of the external sign-in.</summary>
    public const string Scheme = "external-idp";

    public bool Enabled { get; set; }

    /// <summary>OIDC authority, e.g. <c>https://{domain}.ciamlogin.com/{tenantId}/v2.0</c>; discovery is read from it.</summary>
    public string? Authority { get; set; }

    /// <summary>The app registration's client id; ID tokens must carry exactly this <c>aud</c>.</summary>
    public string? ClientId { get; set; }

    /// <summary>The app registration's client secret. From Key Vault: secret <c>SmartAuth--ExternalLogin--ClientSecret</c>.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The exact <c>iss</c> of the IdP's ID tokens (Entra External ID:
    /// <c>https://{tenantId}.ciamlogin.com/{tenantId}/v2.0</c>). Compared
    /// ordinally; it is also the issuer recorded in every binding.
    /// </summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>Shown on the login page: "Sign in with {DisplayName}".</summary>
    public string DisplayName { get; set; } = "Microsoft";

    /// <summary>
    /// Path of the OIDC callback. The redirect URI registered at the IdP is
    /// <c>{SmartAuth:Issuer}{CallbackPath}</c>; it is built from configuration,
    /// never from the request's Host.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>The redirect URI sent to the IdP, set by <see cref="ExternalLogin.Resolve"/>.</summary>
    public Uri RedirectUri { get; set; } = null!;
}

/// <summary>Configuration, wiring and claim mapping of the external sign-in.</summary>
public static class ExternalLogin
{
    /// <summary>The ID token signature algorithms accepted (Entra signs with RS256).</summary>
    public static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha256];

    /// <summary>
    /// The validated options, or null when the external login is disabled.
    /// Throws at startup when it is enabled but incomplete or unsafe.
    /// </summary>
    public static ExternalLoginOptions? Resolve(IConfiguration configuration, IHostEnvironment environment, Uri smartIssuer)
    {
        var options = new ExternalLoginOptions();
        configuration.GetSection(ExternalLoginOptions.SectionName).Bind(options);
        if (!options.Enabled)
            return null;

        const string s = ExternalLoginOptions.SectionName;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Authority)) missing.Add($"{s}:Authority");
        if (string.IsNullOrWhiteSpace(options.ClientId)) missing.Add($"{s}:ClientId");
        if (string.IsNullOrWhiteSpace(options.ClientSecret)) missing.Add($"{s}:ClientSecret");
        if (string.IsNullOrWhiteSpace(options.ExpectedIssuer)) missing.Add($"{s}:ExpectedIssuer");
        if (string.IsNullOrWhiteSpace(options.DisplayName)) missing.Add($"{s}:DisplayName");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"{s}:Enabled is true but {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not set.");

        options.Authority = RequireUri(options.Authority!.Trim(), $"{s}:Authority", environment);
        options.ExpectedIssuer = options.ExpectedIssuer!.Trim();
        RequireUri(options.ExpectedIssuer, $"{s}:ExpectedIssuer", environment);
        options.ClientId = options.ClientId!.Trim();
        options.DisplayName = options.DisplayName.Trim();

        if (string.IsNullOrEmpty(options.CallbackPath) || options.CallbackPath[0] != '/'
            || options.CallbackPath.StartsWith("//", StringComparison.Ordinal)
            || options.CallbackPath.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            throw new InvalidOperationException($"{s}:CallbackPath '{options.CallbackPath}' must be a path starting with a single '/'.");
        }

        options.RedirectUri = new Uri(smartIssuer.AbsoluteUri.TrimEnd('/') + options.CallbackPath);
        return options;
    }

    private static string RequireUri(string value, string key, IHostEnvironment environment)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException($"{key} '{value}' must be an absolute http(s) URI without query, fragment or user info.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
            throw new InvalidOperationException($"{key} '{value}' must use HTTPS outside Development.");

        return value;
    }

    /// <summary>
    /// Registers the OIDC handler: authorization code flow with PKCE and a
    /// nonce; the ID token's signature (IdP JWKS), issuer, audience and
    /// lifetime are validated; the session it signs in to the cookie scheme
    /// is <see cref="SmartSession"/>'s shape and nothing else.
    /// </summary>
    public static AuthenticationBuilder AddExternalLogin(
        this AuthenticationBuilder builder, ExternalLoginOptions external, IHostEnvironment environment)
    {
        var development = environment.IsDevelopment();

        return builder.AddOpenIdConnect(ExternalLoginOptions.Scheme, external.DisplayName, options =>
        {
            options.Authority = external.Authority;
            options.RequireHttpsMetadata = !development;
            options.ClientId = external.ClientId;
            options.ClientSecret = external.ClientSecret;

            options.ResponseType = OpenIdConnectResponseType.Code;
            // The code comes back on a top-level GET, so the correlation and
            // nonce cookies can be SameSite=Lax (form_post would need None).
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.ProtocolValidator.RequireNonce = true;
            options.Scope.Clear();
            options.Scope.Add(OpenIdConnectScope.OpenId);
            options.Scope.Add("profile"); // `name`, for display only

            options.CallbackPath = external.CallbackPath;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.UseTokenLifetime = false;
            options.DisableTelemetry = true;

            // Claim types exactly as in the token (no oid/sub renaming), and
            // no claim actions: OnTokenValidated replaces the principal anyway.
            options.MapInboundClaims = false;
            options.ClaimActions.Clear();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = Algorithms,
                ValidateIssuer = true,
                ValidIssuer = external.ExpectedIssuer,
                ValidateAudience = true,
                ValidAudience = external.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            };

            foreach (var cookie in new[] { options.CorrelationCookie, options.NonceCookie })
            {
                cookie.HttpOnly = true;
                cookie.SameSite = SameSiteMode.Lax;
                cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            }

            options.Events = new OpenIdConnectEvents
            {
                // The redirect URI is configuration ({SmartAuth:Issuer}{CallbackPath}),
                // never the request's scheme and Host. The handler stores this
                // value for the code redemption, so both requests send the same.
                OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = external.RedirectUri.AbsoluteUri;
                    return Task.CompletedTask;
                },

                OnTokenValidated = context =>
                {
                    var audit = context.HttpContext.RequestServices.GetRequiredService<SmartAuthAudit>();
                    var (session, refusal) = MapSession(context.SecurityToken?.Issuer, context.Principal, external.ExpectedIssuer!);
                    if (session is null)
                    {
                        audit.SignInRefused(ExternalLoginOptions.Scheme, refusal!);
                        context.Fail(refusal!);
                        return Task.CompletedTask;
                    }

                    context.Principal = session;
                    audit.SignedIn(ExternalLoginOptions.Scheme, SmartSession.IdentityOf(session)!.Value.ToString());
                    return Task.CompletedTask;
                },

                // Any failure (invalid token, wrong nonce or state, IdP error,
                // user cancelled): no session, back to the login page.
                OnRemoteFailure = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<SmartAuthAudit>()
                        .SignInRefused(ExternalLoginOptions.Scheme, context.Failure?.Message ?? "remote_failure");

                    var returnUrl = LocalUrl.Normalize(context.Properties?.RedirectUri);
                    var target = "/account/login?error=external";
                    if (returnUrl is not null)
                        target += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
                    context.Response.Redirect(target);
                    context.HandleResponse();
                    return Task.CompletedTask;
                },
            };
        });
    }

    /// <summary>
    /// The session for a validated ID token: issuer = the token's <c>iss</c>
    /// (which must equal <paramref name="expectedIssuer"/> exactly), subject =
    /// <c>oid</c>, or <c>sub</c> only when there is no <c>oid</c>; display name
    /// from <c>name</c>. Every other claim — tenant, roles, email, extension
    /// attributes — is dropped.
    /// </summary>
    public static (ClaimsPrincipal? Session, string? Refusal) MapSession(
        string? tokenIssuer, ClaimsPrincipal? idToken, string expectedIssuer)
    {
        if (string.IsNullOrEmpty(tokenIssuer) || !string.Equals(tokenIssuer, expectedIssuer, StringComparison.Ordinal))
            return (null, "id_token_issuer_not_expected");

        var oid = idToken?.FindFirst("oid")?.Value;
        var subject = !string.IsNullOrWhiteSpace(oid) ? oid : idToken?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            return (null, "id_token_has_no_subject");

        return (SmartSession.Principal(tokenIssuer, subject, idToken?.FindFirst("name")?.Value), null);
    }
}

/// <summary>
/// Local redirect targets only: "/x" or "~/x", never "//host", "/\\host",
/// "~//host", "~/\\host" or a URL with control characters (browsers drop
/// them, so "/\t/host" is "//host"). "~/" is the app-relative form of "/"
/// and obeys the same rule: the character after the leading '/' is never
/// '/' or '\\'.
/// </summary>
public static class LocalUrl
{
    public static bool IsLocal(string? url) => Normalize(url) is not null;

    /// <summary>
    /// The local path for <paramref name="url"/> ("~/x" becomes "/x"), or
    /// null when it is not a local URL. Callers redirect to this value only.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Any(char.IsControl))
            return null;

        var path = url[0] == '~' ? url[1..] : url;
        if (path.Length == 0 || path[0] != '/')
            return null;
        if (path.Length > 1 && (path[1] == '/' || path[1] == '\\'))
            return null;
        return path;
    }
}
