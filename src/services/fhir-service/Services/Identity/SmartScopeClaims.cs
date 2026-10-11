using System.Security.Claims;

namespace FhirService.Services.Identity;

/// <summary>
/// The one reading of a SMART token's granted scopes (D-32). Every consumer
/// (SmartScopeEnforcementMiddleware, CallerIdentityResolver, the controllers'
/// backend-caller check, the CRD hook) goes through <see cref="Read"/>, so
/// there is one answer to "what may this caller do".
///
/// <para>Two claims may carry scopes: <c>scope</c> (RFC 8693 / RFC 9068,
/// what smart-auth-service and most authorization servers emit) and
/// <c>scp</c> (Microsoft Entra, Okta). The SMART scheme turns inbound claim
/// mapping off, so both arrive under these literal names.</para>
///
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>Each claim's value is space-delimited (RFC 6749 §3.3). A JSON
///   array claim arrives as one claim per element; each element is split the
///   same way. So <c>"scp": "a b"</c> and <c>"scp": ["a", "b"]</c> mean the
///   same thing.</item>
///   <item>A claim's scopes are the SET of its non-empty space-separated
///   tokens: order, repetition and extra spaces do not matter. Comparison is
///   ordinal, because scope tokens are case-sensitive.</item>
///   <item>Only <c>scope</c>, or only <c>scp</c>: that claim's set.</item>
///   <item>Both, with equal sets: that set.</item>
///   <item>Both, with different sets: a conflict. The token is refused at
///   authentication (401). No union or intersection is ever taken, and
///   <see cref="Read"/> returns NO scopes for it, so any caller that reaches
///   the parser by another route fails closed.</item>
///   <item>Neither: no scopes.</item>
/// </list>
/// </summary>
public static class SmartScopeClaims
{
    /// <summary>The standard scope claim.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>The scope claim Entra and Okta emit.</summary>
    public const string ScpClaim = "scp";

    /// <summary>The scopes a principal holds, and whether its two scope claims disagree.</summary>
    public readonly record struct Result(HashSet<string> Scopes, bool Conflict);

    public static Result Read(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return new Result(new HashSet<string>(StringComparer.Ordinal), false);

        var scope = SetOf(principal, ScopeClaim);
        var scp = SetOf(principal, ScpClaim);

        if (scope is null && scp is null)
            return new Result(new HashSet<string>(StringComparer.Ordinal), false);
        if (scp is null)
            return new Result(scope!, false);
        if (scope is null)
            return new Result(scp, false);

        return scope.SetEquals(scp)
            ? new Result(scope, false)
            : new Result(new HashSet<string>(StringComparer.Ordinal), true);
    }

    /// <summary>The scopes, or none when the two claims conflict.</summary>
    public static HashSet<string> Parse(ClaimsPrincipal? principal) => Read(principal).Scopes;

    /// <summary>
    /// The set of scopes in every claim of <paramref name="claimType"/>, or null
    /// when the principal carries no such claim at all. A claim that is present
    /// but empty is an empty set, not an absent claim.
    /// </summary>
    private static HashSet<string>? SetOf(ClaimsPrincipal principal, string claimType)
    {
        HashSet<string>? set = null;
        foreach (var claim in principal.FindAll(claimType))
        {
            set ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                set.Add(value);
        }
        return set;
    }
}
