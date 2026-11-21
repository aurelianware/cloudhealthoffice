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
///   <item>A claim that is present but empty (<c>""</c> or <c>[]</c>) is an
///   empty set, not an absent claim, so <c>"scope": []</c> beside a non-empty
///   <c>scp</c> is a conflict.</item>
///   <item>Claim NAMES match case-insensitively (<c>SCP</c>, <c>Scope</c>),
///   as on main: <see cref="ClaimsIdentity.FindAll(string)"/> compares claim
///   types that way.</item>
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

    /// <param name="principal">The validated principal.</param>
    /// <param name="payloadClaimNames">
    /// The claim names present in the token's payload (case-insensitive), when
    /// the token is at hand. The JWT handler creates NO principal claim for an
    /// empty JSON array, so <c>"scope": []</c> would otherwise read as an
    /// absent claim rather than an empty set, and escape the conflict rule.
    /// Authentication passes these; later readers see only principals that
    /// already passed that check.
    /// </param>
    public static Result Read(ClaimsPrincipal? principal, IReadOnlySet<string>? payloadClaimNames = null)
    {
        if (principal is null)
            return new Result(new HashSet<string>(StringComparer.Ordinal), false);

        var scope = SetOf(principal, ScopeClaim, payloadClaimNames);
        var scp = SetOf(principal, ScpClaim, payloadClaimNames);

        if (scope is null && scp is null)
            return new Result(new HashSet<string>(StringComparer.Ordinal), false);
        if (scp is null)
            return new Result(WithV1Equivalents(scope!), false);
        if (scope is null)
            return new Result(WithV1Equivalents(scp), false);

        return scope.SetEquals(scp)
            ? new Result(WithV1Equivalents(scope), false)
            : new Result(new HashSet<string>(StringComparer.Ordinal), true);
    }

    /// <summary>
    /// Adds the SMART v1 scope each SMART v2 resource scope is exactly
    /// equivalent to (SMART App Launch 2.x §Scopes for requesting FHIR
    /// resources), so every check, which is written in v1 terms, reads both.
    ///
    /// <list type="bullet">
    ///   <item><c>{ctx}/{T}.rs</c> (read and search) is <c>{ctx}/{T}.read</c>.</item>
    ///   <item><c>{ctx}/{T}.cud</c> (create, update and delete) is <c>{ctx}/{T}.write</c>.</item>
    ///   <item><c>{ctx}/{T}.cruds</c> is both.</item>
    /// </list>
    ///
    /// Only an exact equivalent is added. A v2 scope narrower than any v1
    /// scope (<c>.r</c> without <c>s</c>, <c>.c</c> alone, or a granular
    /// <c>?category=</c> scope) adds nothing, so it grants nothing here: fail
    /// closed rather than widen a grant the client did not receive.
    /// </summary>
    internal static HashSet<string> WithV1Equivalents(HashSet<string> scopes)
    {
        List<string>? added = null;
        foreach (var scope in scopes)
        {
            var slash = scope.IndexOf('/');
            var dot = scope.LastIndexOf('.');
            if (slash <= 0 || dot <= slash + 1 || scope.Contains('?'))
                continue;

            var context = scope[..slash];
            if (context is not ("patient" or "user" or "system"))
                continue;

            var permissions = scope[(dot + 1)..];
            if (!IsV2Permissions(permissions))
                continue;

            var prefix = scope[..dot];
            if (permissions.Contains('r') && permissions.Contains('s'))
                (added ??= []).Add(prefix + ".read");
            if (permissions.Contains('c') && permissions.Contains('u') && permissions.Contains('d'))
                (added ??= []).Add(prefix + ".write");
        }

        if (added is null)
            return scopes;

        var result = new HashSet<string>(scopes, StringComparer.Ordinal);
        result.UnionWith(added);
        return result;
    }

    /// <summary>A non-empty subsequence of <c>cruds</c>, in that order, each letter at most once.</summary>
    private static bool IsV2Permissions(string permissions)
    {
        if (permissions.Length is 0 or > 5)
            return false;

        var at = 0;
        foreach (var letter in permissions)
        {
            var next = "cruds".IndexOf(letter, at);
            if (next < 0)
                return false;
            at = next + 1;
        }

        return true;
    }

    /// <summary>The scopes, or none when the two claims conflict.</summary>
    public static HashSet<string> Parse(ClaimsPrincipal? principal) => Read(principal).Scopes;

    /// <summary>
    /// The names of the top-level members of a JWT's payload, compared
    /// case-insensitively (as <see cref="ClaimsIdentity.FindAll(string)"/>
    /// compares claim types). Empty for anything that is not a readable JWS.
    /// </summary>
    public static IReadOnlySet<string> PayloadClaimNames(Microsoft.IdentityModel.JsonWebTokens.JsonWebToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(token.EncodedPayload);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var member in doc.RootElement.EnumerateObject())
                    names.Add(member.Name);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or System.Text.Json.JsonException)
        {
            // The handler already parsed and validated this payload; an
            // unreadable one here leaves only the principal's claims to go on.
        }
        return names;
    }

    /// <summary>
    /// The set of scopes in every claim of <paramref name="claimType"/>, or null
    /// when the token carries no such claim at all. A claim that is present
    /// but empty — <c>""</c>, or <c>[]</c>, which leaves no principal claim but
    /// is in <paramref name="payloadClaimNames"/> — is an empty set, not an
    /// absent claim.
    /// </summary>
    private static HashSet<string>? SetOf(
        ClaimsPrincipal principal, string claimType, IReadOnlySet<string>? payloadClaimNames)
    {
        HashSet<string>? set = payloadClaimNames?.Contains(claimType) == true
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        foreach (var claim in principal.FindAll(claimType))
        {
            set ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                set.Add(value);
        }
        return set;
    }
}
