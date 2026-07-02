using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using SmartAuthService.Models;
using SmartAuthService.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Controllers;

/// <summary>
/// Handles SMART on FHIR authorization flows.
///
/// Standalone launch:
///   App → GET /connect/authorize?response_type=code&client_id=...&scope=openid+patient/*.read
///       → user logs in → code returned → POST /connect/token → access token with patient binding
///
/// EHR launch:
///   EHR → POST /launch → receives launch token
///   EHR → redirects app to → GET /connect/authorize?...&launch={token}&scope=launch/patient+...
///       → auth server extracts patient/encounter context → binds to token
/// </summary>
[ApiController]
public class AuthorizationController : ControllerBase
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly ILaunchContextStore _launchContextStore;
    private readonly ILogger<AuthorizationController> _logger;

    public AuthorizationController(
        IOpenIddictApplicationManager applicationManager,
        ILaunchContextStore launchContextStore,
        ILogger<AuthorizationController> logger)
    {
        _applicationManager = applicationManager;
        _launchContextStore = launchContextStore;
        _logger = logger;
    }

    // ── Authorization endpoint ────────────────────────────────────────────────

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("SMART authorization request is missing.");

        // Check whether the user is already logged in via the consent cookie
        var cookieAuth = await HttpContext.AuthenticateAsync(
            CookieAuthenticationDefaults.AuthenticationScheme);

        if (!cookieAuth.Succeeded || cookieAuth.Principal == null)
        {
            // Redirect to login, preserving the full authorization request URL
            var returnUrl = Request.PathBase + Request.Path + QueryString.Create(
                Request.HasFormContentType
                    ? Request.Form.ToList()
                    : Request.Query.ToList());

            return Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl },
                CookieAuthenticationDefaults.AuthenticationScheme);
        }

        // ── Resolve EHR launch context (if present) ───────────────────────────
        var launchToken = request.GetParameter("launch")?.ToString();
        string? boundPatientId = null;
        string? boundEncounterId = null;
        string? boundPractitionerId = null;

        if (!string.IsNullOrEmpty(launchToken))
        {
            // Consume single-use launch token
            var launchCtx = await _launchContextStore.ConsumeAsync(launchToken, ct);
            if (launchCtx == null)
            {
                _logger.LogWarning("EHR launch token not found or expired: {Token}", SanitizeForLog(launchToken));
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            boundPatientId = launchCtx.PatientId;
            boundEncounterId = launchCtx.EncounterId;
            boundPractitionerId = launchCtx.PractitionerId;

            _logger.LogInformation(
                "EHR launch resolved — patient: {PatientId}, encounter: {EncounterId}",
                SanitizeForLog(boundPatientId), SanitizeForLog(boundEncounterId));
        }

        // ── Build the authenticated principal ─────────────────────────────────
        var userId = cookieAuth.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? cookieAuth.Principal.FindFirstValue(ClaimTypes.Name)
                  ?? "unknown";

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.AddClaim(new Claim(Claims.Subject, userId)
            .SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));

        identity.AddClaim(new Claim(Claims.Name, userId)
            .SetDestinations(Destinations.IdentityToken));

        // ── Inject SMART context claims ───────────────────────────────────────
        // These are included in access tokens only, per SMART App Launch Framework §7.
        if (boundPatientId != null)
        {
            identity.AddClaim(new Claim(SmartClaims.Patient, boundPatientId)
                .SetDestinations(Destinations.AccessToken));
        }
        else if (request.HasScope(SmartScopes.LaunchPatient))
        {
            // Standalone launch with launch/patient scope: use the subject as patient
            // In production, look up the patient record linked to the authenticated user.
            // For now we store the user's subject ID as the patient binding.
            identity.AddClaim(new Claim(SmartClaims.Patient, userId)
                .SetDestinations(Destinations.AccessToken));
            _logger.LogInformation("Standalone launch: bound patient={UserId}", SanitizeForLog(userId));
        }

        if (boundEncounterId != null)
        {
            identity.AddClaim(new Claim(SmartClaims.Encounter, boundEncounterId)
                .SetDestinations(Destinations.AccessToken));
        }

        if (boundPractitionerId != null)
        {
            identity.AddClaim(new Claim(SmartClaims.FhirUser,
                    $"Practitioner/{boundPractitionerId}")
                .SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());

        // Set the FHIR API as the audience for the access token
        principal.SetResources("fhir-api");

        _logger.LogInformation(
            "Issuing authorization code — subject: {Subject}, scopes: {Scopes}",
            SanitizeForLog(userId), SanitizeForLog(string.Join(" ", request.GetScopes())));

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ── Token endpoint ────────────────────────────────────────────────────────
    // OpenIddict handles authorization_code, refresh_token, and client_credentials
    // exchanges automatically.  We use passthrough only to add custom claims that
    // may need refreshing (e.g. updated patient context on refresh grant).

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Token request is missing.");

        ClaimsPrincipal principal;

        // ── authorization_code or refresh_token ──────────────────────────────
        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // Retrieve the principal stored in the authorization code / refresh token.
            // OpenIddict has already validated the grant and PKCE verifier at this point.
            var result = await HttpContext.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            if (!result.Succeeded || result.Principal == null)
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            principal = result.Principal;

            // Refresh token rotation: ensure the patient claim destination is preserved
            foreach (var claim in principal.Claims)
            {
                if (claim.GetDestinations().IsEmpty)
                    claim.SetDestinations(Destinations.AccessToken);
            }

            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // ── client_credentials (system/*.read) ───────────────────────────────
        if (request.IsClientCredentialsGrantType())
        {
            var app = await _applicationManager.FindByClientIdAsync(request.ClientId!, ct);
            if (app == null)
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            var identity = new ClaimsIdentity(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            identity.AddClaim(new Claim(Claims.Subject, request.ClientId!)
                .SetDestinations(Destinations.AccessToken));

            principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());
            principal.SetResources("fhir-api");

            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return BadRequest(new { error = Errors.UnsupportedGrantType });
    }

    // ── Logout endpoint ───────────────────────────────────────────────────────

    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ── UserInfo endpoint ─────────────────────────────────────────────────────

    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    public IActionResult Userinfo()
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = User.FindFirstValue(Claims.Subject) ?? string.Empty
        };

        var patient = User.FindFirstValue(SmartClaims.Patient);
        if (patient != null) claims[SmartClaims.Patient] = patient;

        var fhirUser = User.FindFirstValue(SmartClaims.FhirUser);
        if (fhirUser != null) claims[SmartClaims.FhirUser] = fhirUser;

        return Ok(claims);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces control characters (including CR/LF/tab/NUL) with '_' in user-supplied
    /// strings and truncates the sanitized result to 256 characters before they appear
    /// in log messages, preventing log-forging/log-injection (CodeQL rule
    /// cs/log-forging). Uses char.IsControl() so that CodeQL's sanitizer recognition
    /// picks it up correctly — a simple Replace("\r","").Replace("\n","") is not
    /// sufficient for CodeQL to recognise the value as sanitized.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        const int maxLength = 256;
        var buffer = new System.Text.StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            if (buffer.Length == maxLength) break;
            buffer.Append(char.IsControl(ch) ? '_' : ch);
        }
        return buffer.ToString();
    }
}
