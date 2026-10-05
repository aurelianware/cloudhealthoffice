using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>
/// The shape of a sign-in session, shared by every login (the Development
/// login and the external OIDC login), so they cannot drift apart.
///
/// A session carries the identity's issuer (<see cref="IssuerClaim"/>), its
/// immutable subject (<see cref="ClaimTypes.NameIdentifier"/>) and, for
/// display only, a name. It carries no tenant, role, member, patient or email:
/// those are looked up from the bindings when a token is issued.
/// </summary>
public static class SmartSession
{
    /// <summary>Session claim naming the identity's issuer.</summary>
    public const string IssuerClaim = "cho_idp_iss";

    public static ClaimsPrincipal Principal(string issuer, string subject, string? displayName = null)
    {
        if (string.IsNullOrEmpty(issuer)) throw new ArgumentException("An issuer is required.", nameof(issuer));
        if (string.IsNullOrEmpty(subject)) throw new ArgumentException("A subject is required.", nameof(subject));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new(IssuerClaim, issuer),
        };
        if (!string.IsNullOrWhiteSpace(displayName))
            claims.Add(new Claim(ClaimTypes.Name, displayName));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static SmartIdentity? IdentityOf(ClaimsPrincipal? principal)
    {
        var issuer = principal?.FindFirstValue(IssuerClaim);
        var subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject)
            ? null
            : new SmartIdentity(issuer, subject);
    }
}
