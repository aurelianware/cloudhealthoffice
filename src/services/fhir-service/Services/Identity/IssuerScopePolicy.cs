using System.Security.Claims;

namespace FhirService.Services.Identity;

/// <summary>
/// G-39: confines the scopes a trusted issuer can grant to the
/// <see cref="TrustedIssuerOptions.AllowedScopes"/> an administrator wrote down
/// for it.
///
/// <para>
/// Runs once, at authentication, after the D-32 <c>scope</c>/<c>scp</c>
/// conflict check and before anything reads the scopes. It rewrites the
/// principal's scope claims in place rather than refusing the token, so a
/// caller whose issuer granted one scope too many still gets the scopes the
/// issuer may grant, and the scope it may not grant is answered like any
/// other missing scope: 403 at <c>SmartScopeEnforcementMiddleware</c>.
/// Every later reader (<see cref="SmartScopeClaims.Read"/>, so the middleware,
/// <see cref="CallerIdentityResolver"/> and the controllers) sees only the
/// narrowed set, because there is no other copy of the claims.
/// </para>
///
/// <para>
/// Both claims are narrowed by the same list, so two claims that held the
/// same set still hold the same set, and the D-32 rule is unaffected. A claim
/// left with no scopes stays present and empty rather than disappearing,
/// which keeps "present but empty" and "absent" distinct as D-32 requires.
/// </para>
/// </summary>
public static class IssuerScopePolicy
{
    /// <summary>
    /// Removes from <paramref name="principal"/>'s <c>scope</c> and <c>scp</c>
    /// claims every scope <paramref name="issuer"/> may not grant. Returns how
    /// many scope tokens were removed. An issuer with no allowlist is
    /// untouched.
    /// </summary>
    public static int Narrow(ClaimsPrincipal principal, TrustedIssuerOptions issuer)
    {
        if (issuer.AllowedScopes.Count == 0)
            return 0;

        var allowed = new HashSet<string>(issuer.AllowedScopes, StringComparer.Ordinal);
        var removed = 0;

        foreach (var identity in principal.Identities)
        {
            // FindAll compares claim types case-insensitively, as SmartScopeClaims does.
            var scopeClaims = identity.FindAll(SmartScopeClaims.ScopeClaim)
                .Concat(identity.FindAll(SmartScopeClaims.ScpClaim))
                .ToList();

            foreach (var claim in scopeClaims)
            {
                var tokens = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var kept = tokens.Where(allowed.Contains).ToArray();
                if (kept.Length == tokens.Length)
                    continue;

                removed += tokens.Length - kept.Length;
                identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(
                    claim.Type, string.Join(' ', kept), claim.ValueType, claim.Issuer, claim.OriginalIssuer, identity));
            }
        }

        return removed;
    }
}
