namespace FhirService.Services.Identity;

/// <summary>
/// One trusted authorization server, and everything CHO is willing to believe
/// from it. Every field here is administrator-supplied; nothing in a token can
/// add to or alter it.
/// </summary>
public sealed class TrustedIssuerOptions
{
    /// <summary>
    /// The exact <c>iss</c> value this entry trusts. Compared ordinally after a
    /// single canonical parse — never by prefix, suffix, or case-insensitive
    /// match, because <c>https://idp.example.com.attacker.test</c> and
    /// <c>https://idp.example.com/../evil</c> both defeat those.
    /// </summary>
    public string Issuer { get; set; } = "";

    /// <summary>
    /// Audiences CHO accepts from this issuer. At least one is required: a
    /// token issued for another API of the same tenant at the same IdP is a
    /// valid, correctly signed token that was never meant for CHO.
    /// </summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>
    /// OIDC discovery document. Defaults to
    /// <c>{Issuer}/.well-known/openid-configuration</c>. An explicit value is
    /// still checked against <see cref="Issuer"/> and the origin policy.
    /// </summary>
    public string? DiscoveryUrl { get; set; }

    /// <summary>
    /// JWKS endpoint, when the issuer publishes no discovery document. Skips
    /// discovery entirely rather than guessing a path.
    /// </summary>
    public string? JwksUri { get; set; }

    /// <summary>
    /// Tenants this issuer may authenticate. Empty means every tenant.
    ///
    /// A populated list is what stops one customer's IdP from minting tokens
    /// for another customer's data: the issuer is trusted to say who the caller
    /// is, but not to say which tenant they belong to unless CHO's own
    /// configuration agrees.
    /// </summary>
    public List<string> Tenants { get; set; } = [];

    /// <summary>
    /// Signing algorithms accepted from this issuer. Asymmetric only — see
    /// <see cref="SupportedAlgorithms"/>.
    /// </summary>
    public List<string> AllowedAlgorithms { get; set; } = [];

    /// <summary>Which claims carry which identity, for this issuer.</summary>
    public IssuerClaimMappingOptions Claims { get; set; } = new();

    /// <summary>
    /// Hosts other than the issuer's own that may serve its JWKS. Empty means
    /// same-origin only. Some managed IdPs legitimately serve keys from a
    /// sibling CDN host, so a blanket refusal would be wrong — but the
    /// allowance has to be written down by an administrator, not taken from
    /// whatever the discovery document happens to point at.
    /// </summary>
    public List<string> AdditionalJwksHosts { get; set; } = [];

    /// <summary>Development escape hatch for a local IdP over plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The only algorithms CHO will accept, in any mode. Asymmetric signatures
    /// exclusively:
    ///
    ///   - <c>none</c> is absent, so an unsigned token can never validate.
    ///   - HMAC (HS256/384/512) is absent deliberately. A symmetric verifier
    ///     will happily accept a token signed with a *public* key as the shared
    ///     secret — the classic alg-confusion attack. Since a resource server
    ///     validating a third-party IdP only ever holds public keys, admitting
    ///     HMAC has no legitimate use here and one catastrophic misuse.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedAlgorithms =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "RS256", "RS384", "RS512",
            "PS256", "PS384", "PS512",
            "ES256", "ES384", "ES512",
        };

    /// <summary>Algorithms in effect: the configured subset, or all supported.</summary>
    public IReadOnlyList<string> EffectiveAlgorithms()
        => AllowedAlgorithms.Count > 0
            ? AllowedAlgorithms
            : SupportedAlgorithms.OrderBy(a => a, StringComparer.Ordinal).ToList();

    /// <summary>The issuer as a parsed URI, or null when it is not a valid absolute URI.</summary>
    public Uri? IssuerUri()
        => Uri.TryCreate(Issuer, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>Where discovery should be fetched from, absent an explicit override.</summary>
    public string EffectiveDiscoveryUrl()
        => !string.IsNullOrWhiteSpace(DiscoveryUrl)
            ? DiscoveryUrl!
            : $"{Issuer.TrimEnd('/')}/.well-known/openid-configuration";

    public void Validate(bool isDevelopmentHost)
    {
        if (string.IsNullOrWhiteSpace(Issuer))
            throw new SmartTrustValidationException("A trusted issuer entry has no Issuer.");

        var uri = IssuerUri();
        if (uri == null)
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' is not an absolute URI.");
        }

        // A token's `iss` is compared as a string, so an issuer carrying a
        // query or fragment invites a match that differs only in a part the
        // operator never intended to pin.
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' must not carry a query string or fragment.");
        }

        var httpsRequired = RequireHttpsMetadata && !isDevelopmentHost;
        if (httpsRequired && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' must use HTTPS outside development.");
        }

        if (!isDevelopmentHost && !RequireHttpsMetadata)
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' sets RequireHttpsMetadata=false, which is a "
                + "development-only setting. Token and key material would travel in clear text.");
        }

        if (Audiences.Count == 0 || Audiences.All(string.IsNullOrWhiteSpace))
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' has no Audiences. Without one, a token minted for "
                + "any other API at the same issuer would be accepted by CHO.");
        }

        foreach (var algorithm in AllowedAlgorithms)
        {
            if (!SupportedAlgorithms.Contains(algorithm))
            {
                throw new SmartTrustValidationException(
                    $"Trusted issuer '{Issuer}' allows signing algorithm '{algorithm}', which is "
                    + $"not supported. Supported: {string.Join(", ", SupportedAlgorithms.OrderBy(a => a, StringComparer.Ordinal))}.");
            }
        }

        ValidateMetadataUrl(EffectiveDiscoveryUrl(), "DiscoveryUrl", isDevelopmentHost);
        if (!string.IsNullOrWhiteSpace(JwksUri))
            ValidateMetadataUrl(JwksUri!, "JwksUri", isDevelopmentHost);
    }

    private void ValidateMetadataUrl(string value, string field, bool isDevelopmentHost)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' has a {field} that is not an absolute URI: '{value}'.");
        }

        if (!isDevelopmentHost && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' {field} must use HTTPS outside development.");
        }

        if (!JwksOriginPolicy.IsAllowedHost(uri, this, isDevelopmentHost))
        {
            throw new SmartTrustValidationException(
                $"Trusted issuer '{Issuer}' {field} host '{uri.Host}' is neither the issuer's own "
                + "host nor listed in AdditionalJwksHosts.");
        }
    }
}

/// <summary>
/// Which claim carries which identity, per issuer. Every mapping is opt-in:
/// an unmapped identity is simply absent, never guessed from a conventionally
/// named claim.
/// </summary>
public sealed class IssuerClaimMappingOptions
{
    /// <summary>Claim asserting the CHO tenant. Unset means tenant comes from elsewhere.</summary>
    public string? TenantClaim { get; set; }

    /// <summary>
    /// Claim asserting the caller's provider NPI.
    ///
    /// Deliberately unset by default. An NPI is public information, so a claim
    /// merely *named* <c>npi</c> proves nothing — it is only authoritative
    /// because a named issuer CHO already trusts was configured to assert it.
    /// Leaving this unset keeps the existing corroborating-key behaviour rather
    /// than manufacturing an identity binding out of an unverified string.
    /// </summary>
    public string? ProviderNpiClaim { get; set; }

    /// <summary>Claim carrying a FHIR Practitioner reference or id.</summary>
    public string? PractitionerClaim { get; set; }

    /// <summary>SMART <c>fhirUser</c> claim, when the issuer emits one.</summary>
    public string? FhirUserClaim { get; set; }

    /// <summary>Claim carrying the patient a patient-context token is bound to.</summary>
    public string? PatientClaim { get; set; } = "patient";

    /// <summary>Claim carrying the OAuth client id. Falls back to azp then client_id.</summary>
    public string? ClientIdClaim { get; set; }
}
