using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartAuthService.Models;
using SmartAuthService.Services;

namespace SmartAuthService.Controllers;

/// <summary>
/// The sign-in session behind the SMART authorization endpoint, and the
/// enrolment-code redemption that binds a signed-in identity to a member or
/// provider of one tenant.
///
/// The session cookie carries the identity's issuer
/// (<see cref="SmartSession.IssuerClaim"/>), its subject
/// (<see cref="ClaimTypes.NameIdentifier"/>) and, for display only, a name
/// (<see cref="SmartSession"/>). It carries no tenant and no patient: those
/// are looked up from the bindings when a token is issued.
///
/// Two logins produce that session:
/// <list type="bullet">
/// <item>the external OIDC login (<c>SmartAuth:ExternalLogin</c>, Microsoft
/// Entra External ID), wired in <see cref="Services.ExternalLogin"/>;</item>
/// <item><see cref="DevelopmentLogin"/>, on a Development host with
/// SmartAuth:DevMode=true and nowhere else.</item>
/// </list>
/// See docs/security/smart-auth-tenancy.md.
/// </summary>
[ApiController]
[AllowAnonymous]
public class AccountController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _environment;
    private readonly ISmartIdentityStore _store;
    private readonly SmartAuthAudit _audit;
    private readonly ILogger<AccountController> _logger;
    private readonly ExternalLoginOptions? _external;

    public AccountController(
        IConfiguration config,
        IHostEnvironment environment,
        ISmartIdentityStore store,
        SmartAuthAudit audit,
        ILogger<AccountController> logger,
        IServiceProvider services)
    {
        _config = config;
        _environment = environment;
        _store = store;
        _audit = audit;
        _logger = logger;
        // Registered only when SmartAuth:ExternalLogin is enabled and valid.
        _external = services.GetService<ExternalLoginOptions>();
    }

    private bool DevelopmentLoginEnabled
        => _environment.IsDevelopment() && _config.GetValue<bool>("SmartAuth:DevMode");

    /// <summary>GET /account/login — the sign-in methods enabled on this host.</summary>
    [HttpGet("~/account/login")]
    public ContentResult Login([FromQuery] string returnUrl = "/")
    {
        returnUrl = LocalUrl.Normalize(returnUrl) ?? "/";
        var safeReturn = System.Web.HttpUtility.HtmlEncode(Uri.EscapeDataString(returnUrl));

        var methods = new List<string>();
        if (_external is not null)
        {
            methods.Add($"""
              <p><a id="external-login" href="/account/external-login?returnUrl={safeReturn}"
                    style="display:inline-block;padding:8px 20px;border:1px solid #333;text-decoration:none">
                Sign in with {System.Web.HttpUtility.HtmlEncode(_external.DisplayName)}</a></p>
            """);
        }

        if (DevelopmentLoginEnabled)
        {
            methods.Add($"""
              <form method="post" action="/account/login?returnUrl={safeReturn}">
                <p><label>Username<br>
                  <input name="username" type="text" required autocomplete="username"
                         style="width:100%;padding:6px;margin-top:4px">
                </label></p>
                <p><label>Password<br>
                  <input name="password" type="password" required autocomplete="current-password"
                         style="width:100%;padding:6px;margin-top:4px">
                </label></p>
                <button type="submit" style="padding:8px 20px">Sign in</button>
              </form>
              <p style="font-size:0.8em;color:#666">
                Development only: any username with password <code>Password123!</code>.
                A username gets a token only once it is bound to a member or provider
                (demo-member and demo-provider are bound in demo-tenant).
              </p>
            """);
        }

        if (methods.Count == 0)
        {
            return SmartSecurityHeaders.Page(Response, Page("Cloud Health Office — SMART Login",
                "<p>No sign-in method is configured for this environment.</p>"));
        }

        var error = HttpContext.Request.Query["error"].FirstOrDefault() switch
        {
            "invalid" => "<p style='color:red'>Invalid credentials.</p>",
            "external" => "<p style='color:red'>Sign-in did not complete. Please try again.</p>",
            _ => "",
        };
        var title = DevelopmentLoginEnabled && _external is null
            ? "Cloud Health Office — SMART Login (Development)"
            : "Cloud Health Office — SMART Login";
        return SmartSecurityHeaders.Page(Response, Page(title, error + string.Join("\n", methods)));
    }

    /// <summary>
    /// GET /account/external-login — starts the external OIDC sign-in, which
    /// comes back through the callback path to the (local) return URL. A
    /// non-local return URL is replaced by "/".
    /// </summary>
    [HttpGet("~/account/external-login")]
    public IActionResult ExternalLogin([FromQuery] string returnUrl = "/")
    {
        if (_external is null)
            return NotFound();

        returnUrl = LocalUrl.Normalize(returnUrl) ?? "/";

        return Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, ExternalLoginOptions.Scheme);
    }

    /// <summary>POST /account/login — Development login; sets the session cookie.</summary>
    [HttpPost("~/account/login")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Login(
        [FromForm] string username,
        [FromForm] string password,
        [FromQuery] string returnUrl = "/")
    {
        returnUrl = LocalUrl.Normalize(returnUrl) ?? "/";

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
            || !DevelopmentLoginEnabled || password != DevelopmentLogin.Password)
        {
            _logger.LogWarning("Failed SMART login attempt for user: {Username}", SmartAuthAudit.Clean(username));
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=invalid");
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            DevelopmentLogin.Principal(username.Trim()),
            new AuthenticationProperties { IsPersistent = false });

        _logger.LogInformation("SMART development login for user: {Username}", SmartAuthAudit.Clean(username));

        // Validated above: never an open redirect.
        return LocalRedirect(returnUrl);
    }

    /// <summary>
    /// GET /account/logout — clears the local session cookie. The external
    /// IdP's own session is left as it is: the next external sign-in may
    /// complete without a prompt, and always creates a fresh local session.
    /// </summary>
    [HttpGet("~/account/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/");
    }

    // ── Enrolment-code redemption ─────────────────────────────────────────────

    /// <summary>GET /account/link — form for the signed-in person to enter their enrolment code.</summary>
    [HttpGet("~/account/link")]
    public async Task<IActionResult> Link()
    {
        if (await SignedInIdentityAsync() is null)
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString("/account/link")}");

        return SmartSecurityHeaders.Page(Response, Page("Link your account", """
              <form method="post" action="/account/link">
                <p><label>Enrolment code<br>
                  <input name="code" type="text" required autocomplete="one-time-code"
                         style="width:100%;padding:6px;margin-top:4px">
                </label></p>
                <button type="submit" style="padding:8px 20px">Link</button>
              </form>
            """));
    }

    /// <summary>
    /// POST /account/link — binds the signed-in identity to the member or
    /// provider the code was issued for, in the tenant that issued it. The
    /// request names neither; the code is the only input.
    /// </summary>
    [HttpPost("~/account/link")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Link([FromForm] string? code, CancellationToken ct)
    {
        var identity = await SignedInIdentityAsync();
        if (identity is null)
            return Unauthorized(new { error = "sign_in_required" });
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "code_required" });

        var outcome = await _store.RedeemEnrolmentAsync(SmartIdentifiers.HashCode(code), identity.Value, ct);
        if (outcome is null)
        {
            _audit.TokenRefused(identity.Value.ToString(), null, "enrolment_code_invalid");
            return BadRequest(new { error = "invalid_or_expired_code" });
        }

        var (result, enrolment) = outcome.Value;
        if (result == BindingWriteResult.AlreadyBound)
        {
            _audit.Redeemed(enrolment.Kind, enrolment.TenantId, identity.Value.ToString(), enrolment.Id, "refused-already-bound");
            return Conflict(new { error = "identity_already_linked" });
        }

        _audit.Redeemed(enrolment.Kind, enrolment.TenantId, identity.Value.ToString(), enrolment.Id, "linked");
        return Ok(new { linked = enrolment.Kind.ToLowerInvariant() });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<SmartIdentity?> SignedInIdentityAsync()
    {
        var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return auth.Succeeded ? SmartSession.IdentityOf(auth.Principal) : null;
    }

    private static string Page(string title, string body) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><title>{System.Web.HttpUtility.HtmlEncode(title)}</title></head>
        <body style="font-family:sans-serif;max-width:400px;margin:80px auto">
          <h2>{System.Web.HttpUtility.HtmlEncode(title)}</h2>
          {body}
        </body>
        </html>
        """;
}

/// <summary>
/// The Development-only login. Its sessions have exactly the shape of the
/// external login's (<see cref="SmartSession"/>), under an issuer that no
/// production binding can name.
/// </summary>
public static class DevelopmentLogin
{
    /// <summary>The issuer of identities the Development login creates. Never valid elsewhere.</summary>
    public const string Issuer = "urn:cho:smart-auth:development-login";

    public const string Password = "Password123!";

    /// <summary>Session claim naming the identity's issuer.</summary>
    public const string IssuerClaim = SmartSession.IssuerClaim;

    public static ClaimsPrincipal Principal(string username) => SmartSession.Principal(Issuer, username, username);

    public static SmartIdentity? IdentityOf(ClaimsPrincipal? principal) => SmartSession.IdentityOf(principal);
}
