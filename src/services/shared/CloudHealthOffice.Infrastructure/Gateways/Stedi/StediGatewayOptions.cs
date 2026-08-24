namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Configuration for <see cref="StediHealthcareGateway"/>, bound from
/// <c>HealthcareTransactions:Gateways:Stedi</c>. It layers on top of the shared
/// <see cref="HealthcareGatewayEndpointOptions"/> shape (BaseUrl/ApiKey/
/// Environment) with Stedi-transport concerns (timeout, retries) and the
/// tenant-safe payer-identifier mapping.
///
/// <code>
/// HealthcareTransactions:
///   DefaultGateway: Stedi
///   Gateways:
///     Stedi:
///       BaseUrl: https://healthcare.us.stedi.com
///       ApiKey: ""            # supplied via env/secret provider, never source control
///       Environment: sandbox  # sandbox | production
///       PayerDirectoryBaseUrl: https://payers.us.stedi.com
///       PayerMap:             # deprecated fallback
///         AETNA: "60054"
///       TenantPayerMap:       # deprecated tenant-scoped fallback
///         tenant-alpha:
///           AETNA: "60055"
/// </code>
///
/// The <see cref="ApiKey"/> is expected to arrive through the existing secret
/// provider / Azure Key Vault configuration layering — it is never committed to
/// source control or checked-in sample configuration.
/// </summary>
public sealed class StediGatewayOptions
{
    /// <summary>Configuration path this binds from.</summary>
    public const string SectionPath = "HealthcareTransactions:Gateways:Stedi";

    /// <summary>Recognised Stedi environments.</summary>
    public static readonly string[] KnownEnvironments = { "sandbox", "test", "production" };

    /// <summary>Base URL of the Stedi Healthcare API host. Defaults to the US host.</summary>
    public string? BaseUrl { get; set; } = "https://healthcare.us.stedi.com";

    /// <summary>
    /// Stedi API key. Supplied via the secret provider / environment, never
    /// source control. Sent in the <c>Authorization</c> header per request.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Stedi environment selector: <c>sandbox</c>/<c>test</c> or
    /// <c>production</c>. Stedi test and production keys are distinct; this makes
    /// the intended environment explicit rather than inferred from the key.
    /// </summary>
    public string Environment { get; set; } = "sandbox";

    /// <summary>
    /// Relative path of the real-time JSON eligibility (270/271) endpoint.
    /// Overridable so a Stedi API version bump does not require a code change.
    /// </summary>
    public string EligibilityPath { get; set; } = "/2024-04-01/change/medicalnetwork/eligibility/v3";

    /// <summary>837P JSON submission path (API version 2024-04-01 / v3).</summary>
    public string ProfessionalClaimPath { get; set; } =
        "/2024-04-01/change/medicalnetwork/professionalclaims/v3/submission";

    /// <summary>837I JSON submission path (API version 2024-04-01 / v1).</summary>
    public string InstitutionalClaimPath { get; set; } =
        "/2024-04-01/change/medicalnetwork/institutionalclaims/v1/submission";

    /// <summary>837D JSON submission path (API version 2024-04-01).</summary>
    public string DentalClaimPath { get; set; } = "/2024-04-01/dental-claims/submission";

    /// <summary>
    /// Host for Stedi Core APIs (Poll Transactions). Separate from
    /// <see cref="BaseUrl"/> because reports live on healthcare.us.stedi.com.
    /// </summary>
    public string CoreBaseUrl { get; set; } = "https://core.us.stedi.com";

    /// <summary>
    /// 277CA Report path template. API version 2024-04-01. Placeholder
    /// <c>{transactionId}</c> is replaced with the Stedi transaction UUID.
    /// </summary>
    public string ClaimAcknowledgmentReportPath { get; set; } =
        "/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277";

    /// <summary>Poll Transactions path (Core API 2023-08-01).</summary>
    public string PollTransactionsPath { get; set; } = "/2023-08-01/polling/transactions";

    /// <summary>When true, a hosted poller discovers inbound 277CAs. Default false.</summary>
    public bool ClaimAcknowledgmentPollingEnabled { get; set; }

    public bool ClaimAcknowledgmentPollingOnStartup { get; set; }

    /// <summary>Polling interval in seconds. Default 60.</summary>
    public int ClaimAcknowledgmentPollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Header name Stedi is configured to send (API Keys credential set).
    /// Stedi does not HMAC-sign claim-response webhooks.
    /// </summary>
    public string WebhookCredentialHeaderName { get; set; } = "Authorization";

    /// <summary>
    /// Shared secret Stedi sends in <see cref="WebhookCredentialHeaderName"/>.
    /// Distinct from <see cref="ApiKey"/> (outbound Stedi API). Fail-closed
    /// when empty: inbound webhooks are rejected.
    /// </summary>
    public string? WebhookCredentialValue { get; set; }

    public int WebhookMaxPayloadBytes { get; set; } = 65536;

    /// <summary>
    /// Base URL of Stedi's Payers API. Defaults to the host documented for
    /// <c>GET /2024-04-01/payers</c>. Separate from <see cref="BaseUrl"/>
    /// because eligibility and the payer directory are published on different
    /// hosts.
    /// </summary>
    public string PayerDirectoryBaseUrl { get; set; } = "https://payers.us.stedi.com";

    /// <summary>
    /// Relative path of the List Payers JSON endpoint (API version 2024-04-01).
    /// </summary>
    public string PayerDirectoryPath { get; set; } = "/2024-04-01/payers";

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retries for transient failures (total attempts =
    /// MaxRetries + 1). Only transient categories are retried.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Deprecated fallback: global mapping of Cloud Health Office canonical
    /// payer id to Stedi <c>tradingPartnerServiceId</c>. The canonical payer
    /// reference service is the primary resolver; this map is consulted only
    /// when the directory has no match. Case-insensitive keys.
    /// </summary>
    public Dictionary<string, string> PayerMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deprecated fallback: per-tenant payer maps, keyed by tenant id. Prefer
    /// <c>PayerTenantOverride</c> records. A tenant's entries are only ever
    /// consulted for that tenant.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> TenantPayerMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when this is a production Stedi environment.</summary>
    public bool IsProduction =>
        string.Equals(Environment?.Trim(), "production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validate required configuration and return a list of actionable error
    /// messages. Empty when the configuration is usable. The API key value is
    /// never included in any message.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BaseUrl) ||
            !Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            errors.Add(
                "HealthcareTransactions:Gateways:Stedi:BaseUrl is required and must be an absolute URL " +
                "(e.g. https://healthcare.us.stedi.com).");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            errors.Add(
                "HealthcareTransactions:Gateways:Stedi:ApiKey is required. Supply it via an environment " +
                "variable or the secret provider / Key Vault — do not commit it to source control.");
        }

        if (string.IsNullOrWhiteSpace(Environment) ||
            !KnownEnvironments.Contains(Environment.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                "HealthcareTransactions:Gateways:Stedi:Environment must be one of " +
                $"[{string.Join(", ", KnownEnvironments)}].");
        }

        if (string.IsNullOrWhiteSpace(EligibilityPath))
        {
            errors.Add("HealthcareTransactions:Gateways:Stedi:EligibilityPath must not be empty.");
        }

        if (TimeoutSeconds <= 0)
        {
            errors.Add("HealthcareTransactions:Gateways:Stedi:TimeoutSeconds must be greater than zero.");
        }

        if (MaxRetries < 0)
        {
            errors.Add("HealthcareTransactions:Gateways:Stedi:MaxRetries must not be negative.");
        }

        return errors;
    }

    public string ResolveClaimAcknowledgmentReportPath(string transactionId)
    {
        var template = string.IsNullOrWhiteSpace(ClaimAcknowledgmentReportPath)
            ? "/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277"
            : ClaimAcknowledgmentReportPath;
        return template.Replace("{transactionId}", Uri.EscapeDataString(transactionId), StringComparison.Ordinal);
    }

    /// <summary>
    /// Validate an inbound webhook credential using Stedi's documented
    /// mechanism: a caller-configured API-key header (no HMAC).
    /// </summary>
    public bool WebhookCredentialIsValid(string? provided)
    {
        if (string.IsNullOrEmpty(WebhookCredentialValue) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var expected = System.Text.Encoding.UTF8.GetBytes(WebhookCredentialValue);
        var actual = System.Text.Encoding.UTF8.GetBytes(provided);
        return expected.Length == actual.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
