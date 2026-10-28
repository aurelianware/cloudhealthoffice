namespace FhirService.Services.SmartBackend;

/// <summary>
/// Cloud Health Office as a SMART Backend Services <em>client</em> of other
/// servers (G-42 outbound): the keys CHO signs its client assertions with, and
/// one registration per server CHO calls. Bound from
/// <see cref="SectionName"/>.
///
/// The keys are CHO's single client identity: every registration signs with
/// the active key, and <c>GET /fhir/r4/.well-known/smart-backend-jwks.json</c>
/// publishes all of them (public parts only) so a peer can register that URL
/// as CHO's <c>jwks_uri</c> and follow a rotation without being told.
/// </summary>
public sealed class SmartBackendClientOptions
{
    public const string SectionName = "SmartBackendClients";

    /// <summary>
    /// CHO's private signing keys. The first entry not in
    /// <see cref="SmartBackendSigningKeyOptions.Standby"/> signs; the rest are
    /// published only. Secrets: supply them from the secret store (environment
    /// <c>SmartBackendClients__SigningKeys__0__PrivateKeyPem</c>) or a mounted
    /// file (<see cref="SmartBackendSigningKeyOptions.Path"/>), never from
    /// <c>appsettings</c>.
    /// </summary>
    public List<SmartBackendSigningKeyOptions> SigningKeys { get; set; } = new();

    /// <summary>
    /// Registrations with other servers, keyed by the name callers use (for
    /// Payer-to-Payer, the directory entry's <c>CredentialKey</c>).
    /// </summary>
    public Dictionary<string, SmartBackendRegistrationOptions> Clients { get; set; } = new();

    /// <summary>
    /// A cached access token is replaced this long before it expires, so a call
    /// started just before expiry does not reach the peer with a dead token.
    /// </summary>
    public int RefreshSkewSeconds { get; set; } = 60;

    /// <summary>
    /// Lifetime of each client assertion. SMART caps it at five minutes; the
    /// default leaves a minute for the peer's clock.
    /// </summary>
    public int AssertionLifetimeSeconds { get; set; } = 240;

    /// <summary>
    /// Allows <c>http://</c> token endpoints. Off by default; for a local
    /// development peer only.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }
}

/// <summary>One private key CHO signs client assertions with.</summary>
public sealed class SmartBackendSigningKeyOptions
{
    /// <summary>PEM private key (PKCS#8, PKCS#1 RSA, or SEC1 EC).</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Path of a PEM file (e.g. a mounted secret), instead of <see cref="PrivateKeyPem"/>.</summary>
    public string? Path { get; set; }

    /// <summary>
    /// <c>RS384</c> or <c>ES384</c> (what SMART requires servers to accept);
    /// <c>RS256</c>/<c>ES256</c>/<c>RS512</c>/<c>ES512</c> also work. Inferred
    /// from the key when empty: RSA → RS384, EC P-384 → ES384, P-256 → ES256,
    /// P-521 → ES512.
    /// </summary>
    public string? Algorithm { get; set; }

    /// <summary>The <c>kid</c>. Defaults to the key's RFC 7638 thumbprint.</summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// Published but not used to sign: add a new key in standby, let peers pick
    /// it up from the JWKS, then make it active; keep the old one in standby
    /// until assertions signed with it can no longer be in flight.
    /// </summary>
    public bool Standby { get; set; }
}

/// <summary>CHO's registration as a backend client of one server.</summary>
public sealed class SmartBackendRegistrationOptions
{
    /// <summary>The client id the server assigned CHO (the assertion's <c>iss</c> and <c>sub</c>).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The server's token endpoint (the assertion's <c>aud</c>), from its
    /// <c>.well-known/smart-configuration</c>. HTTPS, no user info, query or
    /// fragment.
    /// </summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>Space-separated scopes to request, e.g. <c>system/*.read</c>. Omitted when empty.</summary>
    public string? Scope { get; set; }
}
