using System.Security.Claims;
using System.Text.Encodings.Web;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Portal.Services;

/// <summary>How people sign in to the portal (<c>Authentication:Mode</c>).</summary>
public static class PortalAuthMode
{
    public const string ConfigurationKey = "Authentication:Mode";
    public const string Entra = "Entra";

    /// <summary>Development-only built-in demo user, development-signed tokens.</summary>
    public const string LocalDemo = "LocalDemo";

    /// <summary>
    /// The portal sits behind an identity-aware proxy (Cloudflare Access, or
    /// Google Cloud IAP with <c>Authentication:CloudflareAccess:Provider</c> =
    /// <c>GoogleIap</c>), and the person it signed in is the portal user. The token service turns their Access token into a
    /// CHO token, for the tenants it lets Access sign in to.
    /// </summary>
    public const string CloudflareAccess = "CloudflareAccess";

    public const string CloudflareAccessSignInPath = "/access/sign-in";

    /// <summary>Cloudflare serves this on every Access-protected hostname; it ends the Access session.</summary>
    public const string CloudflareAccessSignOutPath = "/cdn-cgi/access/logout";

    public static string Mode(IConfiguration configuration) => configuration[ConfigurationKey] ?? Entra;

    public static bool IsLocalDemo(IConfiguration configuration)
        => string.Equals(Mode(configuration), LocalDemo, StringComparison.OrdinalIgnoreCase);

    public static bool IsCloudflareAccess(IConfiguration configuration)
        => string.Equals(Mode(configuration), CloudflareAccess, StringComparison.OrdinalIgnoreCase);

    public static string SignInPath(IConfiguration configuration)
        => IsLocalDemo(configuration) ? "/local-demo/sign-in"
            : IsCloudflareAccess(configuration) ? CloudflareAccessSignInPath
            : "/MicrosoftIdentity/Account/SignIn";

    /// <summary>Google Cloud IAP clears its session cookie on any path with this query.</summary>
    public const string GoogleIapSignOutPath = "/?gcp-iap-mode=CLEAR_LOGIN_COOKIE";

    public static string SignOutPath(IConfiguration configuration)
        => IsLocalDemo(configuration) ? "/local-demo/sign-out"
            : IsCloudflareAccess(configuration)
                ? (IsGoogleIap(configuration) ? GoogleIapSignOutPath : CloudflareAccessSignOutPath)
            : "/MicrosoftIdentity/Account/SignOut";

    /// <summary>
    /// The CloudflareAccess mode behind Google Cloud IAP instead of Cloudflare
    /// (<c>Authentication:CloudflareAccess:Provider</c> = <c>GoogleIap</c>).
    /// </summary>
    public static bool IsGoogleIap(IConfiguration configuration)
        => string.Equals(configuration[CloudflareAccessPortalRegistration.SectionName + ":Provider"],
            nameof(EdgeAccessProvider.GoogleIap), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Signs in the person Cloudflare Access let through, from the
/// <c>Cf-Access-Jwt-Assertion</c> header Access adds to every request. The
/// header is validated here (team keys, issuer, AUD tag, expiry), so a request
/// that reaches the portal some other way (a port-forward, another route into
/// the cluster) cannot claim to be anyone. The token itself is kept on the
/// principal so the portal can present it to the token service, which
/// validates it again and decides tenant and roles.
/// </summary>
public sealed class CloudflareAccessAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "CloudflareAccess";

    /// <summary>Marks a principal signed in through Cloudflare Access.</summary>
    public const string MarkerClaim = "cho_cf_access";

    /// <summary>The validated Access token, presented to the token service.</summary>
    public const string AssertionClaim = "cf_access_assertion";

    private readonly ICloudflareAccessTokenValidator _validator;
    private readonly CloudflareAccessOptions _access;

    public CloudflareAccessAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ICloudflareAccessTokenValidator validator,
        CloudflareAccessOptions access)
        : base(options, logger, encoder)
    {
        _validator = validator;
        _access = access;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var assertion = Request.Headers[_access.Header].ToString();
        if (string.IsNullOrWhiteSpace(assertion))
            return AuthenticateResult.NoResult();

        var result = await _validator.ValidateAsync(assertion, Context.RequestAborted);
        if (result.Identity is not { } identity)
        {
            Logger.LogWarning("{Proxy} token refused: {Reason}", _access.DisplayName, result.Reason);
            return AuthenticateResult.Fail(_access.DisplayName + " token refused: " + result.Reason);
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.Subject),
            new Claim("sub", identity.Subject),
            new Claim(ClaimTypes.Name, identity.Email),
            new Claim(ClaimTypes.Email, identity.Email),
            new Claim("preferred_username", identity.Email),
            new Claim(MarkerClaim, "true"),
            new Claim(AssertionClaim, assertion),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "text/plain; charset=utf-8";
        await Response.WriteAsync(
            $"Sign in through this portal's {_access.DisplayName} address. " +
            $"Requests that do not come through {_access.DisplayName} are not signed in.");
    }
}

public static class CloudflareAccessPortalRegistration
{
    public const string SectionName = "Authentication:CloudflareAccess";

    public static AuthenticationBuilder AddPortalCloudflareAccess(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<CloudflareAccessOptions>() ?? new CloudflareAccessOptions();
        options.Validate(SectionName);
        services.AddSingleton(options);
        services.AddSingleton<ICloudflareAccessTokenValidator>(_ =>
            CloudflareAccessTokenValidator.Create(options, new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        return services.AddAuthentication(CloudflareAccessAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, CloudflareAccessAuthenticationHandler>(
                CloudflareAccessAuthenticationHandler.SchemeName, _ => { });
    }

    /// <summary>
    /// The sign-in address the portal sends an unauthenticated visitor to. Behind
    /// Access every request is already signed in, so this only returns to the
    /// page; reached any other way it explains where to sign in.
    /// </summary>
    public static void MapCloudflareAccessSignIn(this WebApplication app)
    {
        app.MapGet(PortalAuthMode.CloudflareAccessSignInPath, (HttpContext context) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Text(
                    "Sign in through this portal's Cloudflare Access address. " +
                    "Requests that do not come through Cloudflare Access are not signed in.",
                    "text/plain", statusCode: StatusCodes.Status401Unauthorized);

            var redirect = context.Request.Query["redirectUri"].FirstOrDefault();
            return Results.Redirect(LocalRedirect(redirect, context.Request));
        }).AllowAnonymous();
    }

    /// <summary>Only a path on this portal: an absolute URL to this host becomes its path, anything else "/".</summary>
    public static string LocalRedirect(string? redirectUri, HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
            return "/";
        // Browsers drop tabs and newlines and treat \ as / when parsing a
        // Location, so "/\t/evil" or "/\\evil" would leave the site.
        if (redirectUri.Any(c => char.IsControl(c) || c == '\\'))
            return "/";
        if (redirectUri.StartsWith('/') && !redirectUri.StartsWith("//", StringComparison.Ordinal)
            && !redirectUri.StartsWith("/\\", StringComparison.Ordinal))
            return redirectUri.StartsWith(PortalAuthMode.CloudflareAccessSignInPath, StringComparison.OrdinalIgnoreCase) ? "/" : redirectUri;
        if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return LocalRedirect(uri.PathAndQuery, request);
        return "/";
    }
}
