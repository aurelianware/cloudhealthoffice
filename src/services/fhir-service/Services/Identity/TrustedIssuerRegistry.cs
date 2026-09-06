namespace FhirService.Services.Identity;

/// <summary>
/// The fixed set of authorization servers CHO trusts, resolved by exact issuer.
///
/// Resolution order matters and is deliberately not "try each key until one
/// verifies". A registry that verified against every trusted issuer's keys in
/// turn would let issuer A's signing key authenticate a token claiming to come
/// from issuer B, collapsing per-issuer audience, tenant, and claim-mapping
/// policy into whichever entry happened to hold a matching key. So the issuer
/// is resolved FIRST, from the token's <c>iss</c>, and that one entry then
/// supplies the keys, the audiences, the algorithms, and the claim mapping.
/// </summary>
public sealed class TrustedIssuerRegistry
{
    private readonly Dictionary<string, TrustedIssuerOptions> _byIssuer;

    public TrustedIssuerRegistry(SmartTrustOptions options)
    {
        Mode = options.Mode;
        ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds);
        Issuers = options.NormalizedIssuers();

        // Ordinal, not OrdinalIgnoreCase. `iss` is compared as an exact string
        // by RFC 7519; case-folding it would make Https://IdP.Example.com match
        // an entry the operator wrote in lower case, and any place two spellings
        // of one issuer can both match is a place they can diverge.
        _byIssuer = Issuers.ToDictionary(i => i.Issuer, StringComparer.Ordinal);
    }

    public SmartTrustMode Mode { get; }

    public TimeSpan ClockSkew { get; }

    public IReadOnlyList<TrustedIssuerOptions> Issuers { get; }

    /// <summary>Every trusted issuer string — the <c>ValidIssuers</c> set.</summary>
    public IReadOnlyList<string> IssuerNames => _byIssuer.Keys.ToList();

    /// <summary>
    /// The entry for this exact issuer, or null. Null means untrusted, which is
    /// a 401 — never a prompt to go and discover the issuer's keys.
    /// </summary>
    public TrustedIssuerOptions? Resolve(string? issuer)
        => string.IsNullOrEmpty(issuer) ? null
         : _byIssuer.TryGetValue(issuer, out var entry) ? entry
         : null;

    /// <summary>
    /// Whether this issuer may authenticate this tenant. An issuer with no
    /// Tenants list may authenticate any; one with a list is confined to it,
    /// so a token from customer A's IdP cannot reach customer B's data even if
    /// it carries a tenant claim naming it.
    /// </summary>
    public static bool IssuerMayServeTenant(TrustedIssuerOptions issuer, string? tenantId)
    {
        if (issuer.Tenants.Count == 0) return true;
        if (string.IsNullOrEmpty(tenantId)) return false;
        return issuer.Tenants.Any(t => string.Equals(t, tenantId, StringComparison.Ordinal));
    }
}
