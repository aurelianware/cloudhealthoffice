using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace SmartAuthService.Controllers;

/// <summary>
/// Minimal login/logout UI for the SMART consent flow.
///
/// Sprint 2: accepts any username with password "Password123!" in DevMode.
/// Production path: remove the dev credential check and federate with Azure AD
/// via OIDC (replace with AddOpenIdConnect pointing to the existing CHO tenant).
/// </summary>
[ApiController]
public class AccountController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IConfiguration config, ILogger<AccountController> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>GET /account/login — renders a minimal HTML login form.</summary>
    [HttpGet("~/account/login")]
    public ContentResult Login([FromQuery] string returnUrl = "/")
    {
        var error = HttpContext.Request.Query["error"].FirstOrDefault();
        var errorMsg = error == "invalid" ? "<p style='color:red'>Invalid credentials.</p>" : "";
        var safeReturn = System.Web.HttpUtility.HtmlEncode(returnUrl);

        var html = $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>CHO SMART Login</title></head>
            <body style="font-family:sans-serif;max-width:400px;margin:80px auto">
              <h2>Cloud Health Office — SMART Login</h2>
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
                Dev mode: use any username with password <code>Password123!</code>
              </p>
            </body>
            </html>
            """;

        return Content(html, "text/html");
    }

    /// <summary>POST /account/login — validates credentials and sets the auth cookie.</summary>
    [HttpPost("~/account/login")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Login(
        [FromForm] string username,
        [FromForm] string password,
        [FromQuery] string returnUrl = "/")
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=invalid");

        if (!ValidateCredentials(username, password))
        {
            _logger.LogWarning("Failed SMART login attempt for user: {Username}", SanitizeForLog(username));
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}&error=invalid");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, username),
            new(ClaimTypes.Name, username)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false });

        _logger.LogInformation("SMART login successful for user: {Username}", SanitizeForLog(username));

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool ValidateCredentials(string username, string password)
    {
        if (_config.GetValue<bool>("SmartAuth:DevMode"))
        {
            // Dev mode: accept any non-empty username with the dev password
            return password == "Password123!";
        }

        // Production: delegate to Azure AD or user store
        // TODO Sprint 3: federate via AddOpenIdConnect to Azure AD B2C
        return false;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private bool IsLocalUrl(string url)
        => !string.IsNullOrEmpty(url)
            && ((url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\')))
                || (url.Length > 1 && url[0] == '~' && url[1] == '/'));
}
