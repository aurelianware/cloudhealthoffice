namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>Configuration section <c>CoreConnectivity</c>.</summary>
public sealed class CoreConnectivityOptions
{
    public const string SectionName = "CoreConnectivity";

    /// <summary>Off by default: the CORE endpoint is mapped only when a host turns it on.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// ReceiverID values this host answers to (CHO's CORE identity for the
    /// plan, usually its payer id). Empty accepts any; otherwise another value
    /// gets <c>ReceiverIDIllegal</c>.
    /// </summary>
    public List<string> ReceiverIds { get; set; } = [];

    /// <summary>
    /// How long CHO lets a payload handler run before it answers "unable to
    /// respond at this time" itself. The rule's limit is 20 seconds for the
    /// whole round trip, so the default leaves room for the network and the
    /// envelope.
    /// </summary>
    public TimeSpan ResponseBudget { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Largest request body accepted, in bytes. A real-time 270/276 is a few KB.</summary>
    public long MaxRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Request header carrying the client certificate (URL-encoded PEM) when
    /// TLS ends at the ingress (nginx <c>ssl-client-cert</c>, GCLB
    /// <c>X-Client-Cert</c>...). Unset: only a certificate presented to Kestrel
    /// directly is used. The ingress must verify the chain and must strip any
    /// copy of this header the client sends; CHO trusts it as stated.
    /// </summary>
    public string? ClientCertificateHeader { get; set; }

    /// <summary>
    /// Accept a submitter's CHO OAuth bearer token as its credential (the
    /// OAuth option of Connectivity vC4.0.0, which is voluntary). The token's
    /// client must be registered on a submitter below.
    /// </summary>
    public bool AllowBearerTokens { get; set; } = true;

    /// <summary>Registered submitters (providers, clearinghouses, vendors).</summary>
    public List<CoreSubmitterRegistration> Submitters { get; set; } = [];

    /// <summary>Published downtime and the record of outages, for the CORE system availability rule.</summary>
    public CoreAvailabilityOptions Availability { get; set; } = new();
}

/// <summary>
/// One trading partner allowed to call the CORE endpoint. The tenant a call
/// acts within comes from here, never from the envelope.
/// </summary>
public sealed class CoreSubmitterRegistration
{
    /// <summary>The SenderID the submitter puts on its envelopes.</summary>
    public string SenderId { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Optional label for logs (e.g. the clearinghouse's name).</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// PayloadTypes the submitter may send. Empty allows every request type
    /// the endpoint answers.
    /// </summary>
    public List<string> PayloadTypes { get; set; } = [];

    /// <summary>Username for MIME <c>UserName</c> / SOAP UsernameToken.</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// <see cref="CorePasswordHash"/> of the password
    /// (<c>pbkdf2-sha256$iterations$salt$hash</c>). Keep it in the secret
    /// store, not in plain configuration.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>SHA-256 thumbprints (hex) of the client certificates the submitter uses.</summary>
    public List<string> CertificateThumbprints { get; set; } = [];

    /// <summary>OAuth client ids (token <c>azp</c> / <c>client_id</c>) the submitter calls with.</summary>
    public List<string> OAuthClientIds { get; set; } = [];
}
