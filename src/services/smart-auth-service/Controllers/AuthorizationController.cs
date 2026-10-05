using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using Microsoft.AspNetCore;
using OpenIddict.Server.AspNetCore;
using SmartAuthService.Models;
using SmartAuthService.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Controllers;

/// <summary>
/// Handles SMART on FHIR authorization flows.
///
/// Every token's <c>tenant_id</c> — and a member token's <c>patient</c> —
/// comes from the server-side bindings (<see cref="SmartTokenContextResolver"/>):
/// the signed-in identity's member or provider link, and the client's tenant
/// registration. A user or client without a binding gets no token. Nothing in
/// the authorization request, the token request or a header can choose either.
///
/// Standalone launch (member):
///   App → GET /connect/authorize?...&amp;scope=openid+launch/patient+patient/*.read
///       → member signs in → patient = the member id their identity is bound to
///
/// EHR launch (provider user):
///   CHO caller → POST /launch (CHO token; tenant from that token) → launch token
///   EHR → GET /connect/authorize?...&amp;launch={token}
///       → provider user signs in → patient/encounter from the launch, which must
///         be of the provider's tenant and for this client
///
/// Backend (client_credentials): tenant = the client's registration.
///
/// Consent: before an interactive app gets a code (and, with
/// <c>offline_access</c>, a refresh token), the signed-in person approves that
/// app and that scope set on a consent page (<see cref="SmartConsent"/>). The
/// approval is a permanent OpenIddict authorization for (identity, client,
/// scopes), so the page is shown once per new scope set; every code and token
/// is tied to it, and revoking it ends refresh.
/// </summary>
[ApiController]
public class AuthorizationController : ControllerBase
{
    private readonly SmartTokenContextResolver _resolver;
    private readonly SmartAuthAudit _audit;
    private readonly ILogger<AuthorizationController> _logger;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly SmartConsent _consent;

    public AuthorizationController(
        SmartTokenContextResolver resolver,
        SmartAuthAudit audit,
        ILogger<AuthorizationController> logger,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        SmartConsent consent)
    {
        _resolver = resolver;
        _audit = audit;
        _logger = logger;
        _applications = applications;
        _authorizations = authorizations;
        _consent = consent;
    }

    // ── Authorization endpoint ────────────────────────────────────────────────

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
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

        var identity = SmartSession.IdentityOf(cookieAuth.Principal);
        if (identity is null)
            return Refuse(null, request.ClientId, "session_has_no_identity");

        var scopes = request.GetScopes();

        // The EHR launch (if any) is consumed by the resolver, atomically and
        // only for the provider's tenant and this client, after every other
        // check. A launch token is base64url: one with control characters is
        // refused before it is echoed into the consent form.
        var launchToken = request.GetParameter("launch")?.ToString();
        if (launchToken != null && launchToken.Any(char.IsControl))
            return Refuse(identity.Value.ToString(), request.ClientId, "launch_malformed");

        // ── Consent ──────────────────────────────────────────────────────────
        // Nothing is resolved (and no launch consumed) until the person has
        // approved this client for these scopes.
        var application = await _applications.FindByClientIdAsync(request.ClientId ?? string.Empty, ct);
        if (application is null)
            return Refuse(identity.Value.ToString(), request.ClientId, "client_unknown");
        var applicationId = (await _applications.GetIdAsync(application, ct))!;
        var subject = identity.Value.ToString();

        var authorization = request.HasPromptValue(PromptValues.Consent)
            ? null
            : await FindConsentAsync(subject, applicationId, scopes, ct);
        var approvedNow = false;
        if (authorization is null)
        {
            var decision = Request.HasFormContentType ? Request.Form[SmartConsent.DecisionField].ToString() : null;
            var proof = Request.HasFormContentType ? Request.Form[SmartConsent.TokenField].ToString() : null;
            var proven = !string.IsNullOrEmpty(decision)
                         && _consent.Verify(proof, subject, request.ClientId!, scopes);

            if (proven && decision == SmartConsent.Deny)
                return Refuse(subject, request.ClientId, "consent_denied", "The user did not approve this app.");

            if (!(proven && decision == SmartConsent.Approve))
            {
                if (request.HasPromptValue(PromptValues.None))
                {
                    return ForbidAuthorize(Errors.ConsentRequired, "The user has not approved this app for these scopes.");
                }

                var displayName = await _applications.GetLocalizedDisplayNameAsync(application, ct)
                                  ?? request.ClientId!;
                return SmartSecurityHeaders.Page(Response, SmartConsent.Page(
                    displayName, scopes, OriginalParameters(),
                    _consent.Issue(subject, request.ClientId!, scopes)));
            }

            approvedNow = true;
        }

        var resolution = await _resolver.ResolveInteractiveAsync(
            identity.Value, request.ClientId ?? string.Empty, scopes, launchToken, ct);
        if (resolution.Context is not { } context)
            return Refuse(identity.Value.ToString(), request.ClientId, resolution.Refusal!);

        var principal = SmartTokenContextResolver.CreatePrincipal(context, scopes);

        // Remember the approval: a permanent authorization for (identity,
        // client, scopes). Codes and refresh tokens are tied to it.
        if (approvedNow)
        {
            authorization = await _authorizations.CreateAsync(
                principal, subject, applicationId, AuthorizationTypes.Permanent, scopes, ct);
            _audit.Consented(subject, request.ClientId!, string.Join(" ", scopes));
        }
        principal.SetAuthorizationId(await _authorizations.GetIdAsync(authorization!, ct));
        var name = cookieAuth.Principal.FindFirstValue(ClaimTypes.Name);
        if (!string.IsNullOrEmpty(name))
        {
            ((ClaimsIdentity)principal.Identity!).SetClaim(Claims.Name, name);
            SmartTokenContextResolver.ApplyDestinations(principal);
        }

        _logger.LogInformation(
            "Issuing authorization code — context: {Context}, tenant: {Tenant}, subject: {Subject}, scopes: {Scopes}",
            context.Context, SanitizeForLog(context.TenantId), SanitizeForLog(context.Subject),
            SanitizeForLog(string.Join(" ", scopes)));

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ── Token endpoint ────────────────────────────────────────────────────────

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("Token request is missing.");

        // ── authorization_code or refresh_token ──────────────────────────────
        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // The principal stored in the code / refresh token. OpenIddict has
            // already validated the grant, the client and the PKCE verifier.
            var result = await HttpContext.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            if (!result.Succeeded || result.Principal == null)
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            var principal = result.Principal;

            // The binding the code was issued under must still hold. A revoked
            // link or deleted client registration ends refresh immediately.
            var refusal = await _resolver.RevalidateAsync(principal, request.ClientId, ct);
            if (refusal != null)
            {
                _audit.TokenRefused(principal.GetClaim(Claims.Subject), request.ClientId, refusal);
                return ForbidGrant(Errors.InvalidGrant, "The account or client is no longer bound to this tenant.");
            }

            SmartTokenContextResolver.ApplyDestinations(principal);
            _audit.TokenIssued(principal.GetClaim(SmartTokenContextResolver.ContextClaim)!,
                principal.GetClaim(SmartTokenContextResolver.TenantClaim)!,
                principal.GetClaim(Claims.Subject)!, request.ClientId!);
            return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // ── client_credentials (system/* scopes) ─────────────────────────────
        if (request.IsClientCredentialsGrantType())
        {
            var scopes = request.GetScopes();
            var resolution = await _resolver.ResolveClientCredentialsAsync(request.ClientId!, scopes, ct);
            if (resolution.Context is not { } context)
            {
                _audit.TokenRefused(null, request.ClientId, resolution.Refusal!);
                return ForbidGrant(Errors.UnauthorizedClient, "This client is not registered to a tenant.");
            }

            _audit.TokenIssued(context.Context, context.TenantId, context.Subject, request.ClientId!);
            return SignIn(SmartTokenContextResolver.CreatePrincipal(context, scopes),
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return BadRequest(new { error = Errors.UnsupportedGrantType });
    }

    // ── Logout endpoint ───────────────────────────────────────────────────────

    [HttpGet("~/connect/logout")]
    [HttpPost("~/connect/logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ── UserInfo endpoint ─────────────────────────────────────────────────────

    [Authorize(Policy = SmartAccessTokenPolicy.Name)]
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

    /// <summary>A valid permanent approval of this client covering every requested scope, or null.</summary>
    private async Task<object?> FindConsentAsync(
        string subject, string applicationId, ImmutableArray<string> scopes, CancellationToken ct)
    {
        await foreach (var authorization in _authorizations.FindAsync(
                           subject, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes, ct))
        {
            return authorization;
        }
        return null;
    }

    /// <summary>The authorization request's own parameters, for the consent form to post back.</summary>
    private IEnumerable<KeyValuePair<string, string>> OriginalParameters()
    {
        var source = Request.HasFormContentType
            ? Request.Form.Select(p => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(p.Key, p.Value))
            : Request.Query.Select(p => new KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>(p.Key, p.Value));
        foreach (var (key, values) in source)
        {
            if (key is SmartConsent.DecisionField or SmartConsent.TokenField)
                continue;
            foreach (var value in values)
                yield return new(key, value ?? string.Empty);
        }
    }

    /// <summary>No binding, no token: OAuth access_denied back to the client.</summary>
    private IActionResult Refuse(string? identity, string? clientId, string reason, string? description = null)
    {
        _audit.TokenRefused(identity, clientId, reason);
        return ForbidAuthorize(Errors.AccessDenied, description
            ?? "This account is not linked to a member or provider of a tenant this app is registered with.");
    }

    private IActionResult ForbidAuthorize(string error, string description)
        => Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

    private IActionResult ForbidGrant(string error, string description)
        => Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

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

/// <summary>An access token this server issued, validated by OpenIddict's local validation.</summary>
public static class SmartAccessTokenPolicy
{
    public const string Name = "smart-access-token";
}
