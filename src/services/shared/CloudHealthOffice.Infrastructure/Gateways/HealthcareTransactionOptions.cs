namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Binds from the <c>HealthcareTransactions</c> configuration section.
///
/// <code>
/// HealthcareTransactions:
///   DefaultGateway: Mock
///   Gateways:
///     Stedi:
///       BaseUrl: https://healthcare.us.stedi.com/...
///       ApiKey:  # supplied via Key Vault / secret provider, never source control
///       Environment: sandbox
/// </code>
///
/// Only <see cref="DefaultGateway"/> is required for this release; the
/// per-gateway <see cref="Gateways"/> map exists so future vendors (Stedi,
/// Availity, direct X12) can be configured without a schema change. No real
/// credentials are committed — secrets flow in through the existing secret
/// provider / Key Vault configuration layering.
/// </summary>
public sealed class HealthcareTransactionOptions
{
    public const string SectionName = "HealthcareTransactions";

    /// <summary>
    /// Name of the gateway used when a caller does not request one explicitly.
    /// Matches an <see cref="IHealthcareTransactionGateway.Name"/>. Defaults to
    /// the mock development gateway.
    /// </summary>
    public string DefaultGateway { get; set; } = "Mock";

    /// <summary>
    /// Per-gateway endpoint configuration, keyed by gateway name
    /// (case-insensitive). Empty in this release except for whatever an
    /// operator chooses to configure.
    /// </summary>
    public Dictionary<string, HealthcareGatewayEndpointOptions> Gateways { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Persistence for 837 transmissions, 277CA acknowledgments, and poll cursors.</summary>
    public ClaimLifecycleOptions ClaimLifecycle { get; set; } = new();

    /// <summary>275 attachment MIME/size limits and content-store settings.</summary>
    public ClaimAttachmentOptions ClaimAttachments { get; set; } = new();
}

/// <summary>
/// Connection settings for a single external gateway. Prepared for future
/// vendor onboarding; <see cref="ApiKey"/> is expected to be supplied by the
/// secret provider (Azure Key Vault) rather than appsettings, and is never
/// committed to source control.
/// </summary>
public sealed class HealthcareGatewayEndpointOptions
{
    /// <summary>Base URL of the vendor API.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API key / credential reference, injected via the secret provider.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Vendor environment selector, e.g. "sandbox" or "production".</summary>
    public string? Environment { get; set; }
}
