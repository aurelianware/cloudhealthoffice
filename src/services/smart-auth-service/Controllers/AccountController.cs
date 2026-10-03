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
/// The session cookie carries exactly two facts: the identity's issuer
/// (<see cref="DevelopmentLogin.IssuerClaim"/>) and its subject
/// (<see cref="ClaimTypes.NameIdentifier"/>). It carries no tenant and no
/// patient: those are looked up from the bindings when a token is issued.
///
/// The only login implemented here is <see cref="DevelopmentLogin"/>, which
/// works on a Development host with SmartAuth:DevMode=true and nowhere else.
/// A production member/provider login must federate to an external identity
/// provider; see docs/security/smart-auth-tenancy.md for what it must supply.
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

    public AccountController(
        IConfiguration config,
        IHostEnvironment environment,
        ISmartIdentityStore store,
        SmartAuthAudit audit,
        ILogger<AccountController> logger)
    {
        _config = config;
        _environment = environment;
        _store = store;
        _audit = audit;
        _logger = logger;
    }

    private bool DevelopmentLoginEnabled
        => _environment.IsDevelopment() && _config.GetValue<bool>("SmartAuth:DevMode");

    /// <summary>GET /account/login — renders a minimal HTML login form.</summary>
    [HttpGet("~/account/login")]
    public ContentResult Login([FromQuery] string returnUrl = "/")
    {
        if (!DevelopmentLoginEnabled)
        {
            return Content(Page("Cloud Health Office — SMART Login",
                "<p>No sign-in method is configured for this environment.</p>"), "text/html");
        }

        var error = HttpContext.Request.Query["error"].FirstOrDefault();
        var errorMsg = error == "invalid" ? "<p style='color:red'>Invalid credentials.</p>" : "";
        var safeReturn = System.Web.HttpUtility.HtmlEncode(Uri.EscapeDataString(returnUrl));

        return Content(Page("Cloud Health Office — SMART Login (Development)", $"""
              {errorMsg}
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
            """), "text/html");
    }

    /// <summary>POST /account/login — Development login; sets the session cookie.</summary>
    [HttpPost("~/account/login")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Login(
        [FromForm] string username,
        [FromForm] string password,
        [FromQuery] string returnUrl = "/")
    {
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

        // Validate redirect target to prevent open redirect attacks
        return LocalRedirect(IsLocalUrl(returnUrl) ? returnUrl : "/");
    }

    /// <summary>GET /account/logout — clears the auth cookie.</summary>
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

        return Content(Page("Link your account", """
              <form method="post" action="/account/link">
                <p><label>Enrolment code<br>
                  <input name="code" type="text" required autocomplete="one-time-code"
                         style="width:100%;padding:6px;margin-top:4px">
                </label></p>
                <button type="submit" style="padding:8px 20px">Link</button>
              </form>
            """), "text/html");
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
        return auth.Succeeded ? DevelopmentLogin.IdentityOf(auth.Principal) : null;
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

    private static bool IsLocalUrl(string url)
        => !string.IsNullOrEmpty(url)
            && ((url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')))
                || (url.Length > 1 && url[0] == '~' && url[1] == '/'));
}

/// <summary>
/// The Development-only login and the shape of a sign-in session. A
/// production login (an external OIDC provider) must produce the same two
/// claims from its validated ID token: <see cref="IssuerClaim"/> = the IdP's
/// <c>iss</c>, and <see cref="ClaimTypes.NameIdentifier"/> = its immutable
/// subject (<c>sub</c>, or <c>oid</c> for Entra).
/// </summary>
public static class DevelopmentLogin
{
    /// <summary>The issuer of identities the Development login creates. Never valid elsewhere.</summary>
    public const string Issuer = "urn:cho:smart-auth:development-login";

    public const string Password = "Password123!";

    /// <summary>Session claim naming the identity's issuer.</summary>
    public const string IssuerClaim = "cho_idp_iss";

    public static ClaimsPrincipal Principal(string username)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, username),
            new Claim(ClaimTypes.Name, username),
            new Claim(IssuerClaim, Issuer),
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    public static SmartIdentity? IdentityOf(ClaimsPrincipal? principal)
    {
        var issuer = principal?.FindFirstValue(IssuerClaim);
        var subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject)
            ? null
            : new SmartIdentity(issuer, subject);
    }
}
