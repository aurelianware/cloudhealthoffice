using System.Security.Claims;

namespace FhirService.Services.Identity;

/// <summary>
/// Turns a validated <see cref="ClaimsPrincipal"/> into an
/// <see cref="AuthenticatedCaller"/>, using the claim mapping of the issuer
/// that actually signed the token.
///
/// The mapping is per-issuer, and that is the whole point. A claim called
/// <c>npi</c> is not evidence of anything on its own — NPIs are public, and any
/// IdP can be told to emit any claim. It becomes authoritative only when a named
/// issuer CHO already trusts was configured, by an administrator, to assert it.
/// So this resolver never scans for conventionally named claims: an unmapped
/// identity stays absent, and every downstream check that would have used it
/// keeps its existing, weaker-but-honest behaviour.
/// </summary>
public sealed class CallerIdentityResolver
{
    private readonly TrustedIssuerRegistry _registry;

    public CallerIdentityResolver(TrustedIssuerRegistry registry) => _registry = registry;

    /// <summary>
    /// Resolves the caller, or null when the principal is unauthenticated or
    /// its issuer is not one CHO trusts. Null is a 401: a principal whose
    /// issuer is unknown here should never have authenticated at all, so this
    /// is a defence-in-depth check behind the JWT handler, not the primary one.
    /// </summary>
    public AuthenticatedCaller? Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        var issuerValue = FirstNonEmpty(principal, "iss")
            ?? principal.Claims.FirstOrDefault()?.Issuer;

        var issuer = _registry.Resolve(issuerValue);
        if (issuer == null) return null;

        var map = issuer.Claims;
        var scopes = ParseScopes(principal);
        var callerType = ClassifyCaller(scopes);

        return new AuthenticatedCaller
        {
            Issuer = issuer.Issuer,
            // The JWT name: the SMART scheme does not map `sub` (D-32).
            Subject = FirstNonEmpty(principal, "sub"),
            AuthorizedParty = FirstNonEmpty(principal, "azp"),
            ClientId = Mapped(principal, map.ClientIdClaim)
                       ?? FirstNonEmpty(principal, "client_id", "azp"),
            CallerType = callerType,
            Scopes = scopes,

            // Identity that only a configured mapping can establish.
            ProviderNpi = NormalizeNpi(Mapped(principal, map.ProviderNpiClaim)),
            PractitionerId = StripPrefix("Practitioner/", Mapped(principal, map.PractitionerClaim)),
            FhirUser = Mapped(principal, map.FhirUserClaim),
            TenantClaim = Mapped(principal, map.TenantClaim),

            PatientId = StripPrefix("Patient/",
                Mapped(principal, map.PatientClaim) ?? FirstNonEmpty(principal, "patient")),

            RegisteredPayerId = issuer.IssuesChoClientRegistrations ? RegisteredPayer(principal) : null,
            RegisteredProviderId = issuer.IssuesChoClientRegistrations && callerType == SmartCallerType.System
                ? RegisteredProvider(principal)
                : null,
        };
    }

    /// <summary>The claim CHO's authorization server mints a backend client's registered payer into.</summary>
    public const string PayerIdClaim = "payer_id";

    /// <summary>
    /// The well-formed value of exactly one <c>payer_id</c> claim, or null.
    /// smart-auth-service mints exactly one; a token carrying more (different
    /// or repeated values) is no mapping rather than a choice between them.
    /// Same format rule as smart-auth-service's registration check.
    /// </summary>
    private static string? RegisteredPayer(ClaimsPrincipal principal)
    {
        var values = principal.FindAll(PayerIdClaim).Select(c => c.Value).ToList();
        if (values.Count != 1) return null;
        var value = values[0];
        if (value.Length is 0 or > 64 || !char.IsAsciiLetterOrDigit(value[0])) return null;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? value : null;
    }

    /// <summary>The claim CHO's authorization server mints a backend client's registered provider into.</summary>
    public const string ProviderIdClaim = "provider_id";

    /// <summary>
    /// The value of exactly one <c>provider_id</c> claim that is a FHIR id
    /// (<c>[A-Za-z0-9\-\.]{1,64}</c>, smart-auth-service's registration rule),
    /// or null.
    /// </summary>
    private static string? RegisteredProvider(ClaimsPrincipal principal)
    {
        var values = principal.FindAll(ProviderIdClaim).Select(c => c.Value).ToList();
        if (values.Count != 1) return null;
        var value = values[0];
        return value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            ? value
            : null;
    }

    /// <summary>
    /// The SMART context the granted scopes put the caller in.
    ///
    /// Order is significant. A token holding both patient/ and system/ scopes is
    /// treated as a patient token, because patient context is the one that
    /// CONSTRAINS: classifying it as system would drop the patient binding and
    /// widen the token, whereas classifying it as patient only narrows it.
    /// When a grant is ambiguous, the narrower reading is the safe one.
    /// </summary>
    public static SmartCallerType ClassifyCaller(IReadOnlySet<string> scopes)
    {
        if (scopes.Any(s => s.StartsWith("patient/", StringComparison.Ordinal))) return SmartCallerType.Patient;
        if (scopes.Any(s => s.StartsWith("user/", StringComparison.Ordinal))) return SmartCallerType.User;
        if (scopes.Any(s => s.StartsWith("system/", StringComparison.Ordinal))) return SmartCallerType.System;
        return SmartCallerType.Unknown;
    }

    /// <summary>
    /// The granted scopes, by the one shared rule (<see cref="SmartScopeClaims"/>):
    /// <c>scope</c> or <c>scp</c>, or both when they agree. Empty when they
    /// disagree (such a token is refused at authentication). The same parser
    /// SmartScopeEnforcementMiddleware uses — two parsers that drift are two
    /// different answers to "what may this caller do".
    /// </summary>
    public static HashSet<string> ParseScopes(ClaimsPrincipal user) => SmartScopeClaims.Parse(user);

    private static string? Mapped(ClaimsPrincipal principal, string? claimType)
        => string.IsNullOrWhiteSpace(claimType) ? null : FirstNonEmpty(principal, claimType!);

    private static string? FirstNonEmpty(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var type in claimTypes)
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    /// <summary>
    /// An NPI is ten digits. A mapped claim carrying anything else is dropped
    /// rather than passed along: a malformed value that reached a comparison
    /// would either never match (harmless) or match something it should not
    /// (not), and there is no reading under which honouring it is correct.
    /// </summary>
    private static string? NormalizeNpi(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 10 && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    private static string? StripPrefix(string prefix, string? value)
        => value == null ? null
         : value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..]
         : value;
}
