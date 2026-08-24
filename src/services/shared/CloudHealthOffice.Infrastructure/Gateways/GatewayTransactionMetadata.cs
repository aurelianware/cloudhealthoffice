namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Non-PHI metadata describing a single transaction routed through a
/// healthcare transaction gateway.
///
/// This type is deliberately free of PHI: it carries routing, timing, and
/// outcome information suitable for structured logs, metrics, and audit
/// records. Raw 270/271 (or any X12 / vendor JSON) request and response
/// payloads must <b>never</b> be placed on this object or written to normal
/// application logs.
/// </summary>
public sealed class GatewayTransactionMetadata
{
    /// <summary>Logical gateway/provider name (e.g. "Mock", "Stedi", "Availity").</summary>
    public string GatewayName { get; init; } = string.Empty;

    /// <summary>The HIPAA/X12 transaction type this metadata describes.</summary>
    public HealthcareTransactionType TransactionType { get; init; }

    /// <summary>UTC timestamp when the transaction was submitted to the gateway.</summary>
    public DateTimeOffset SubmittedAtUtc { get; init; }

    /// <summary>UTC timestamp when the gateway completed the transaction, if it did.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Terminal or in-flight status of the transaction.</summary>
    public GatewayTransactionStatus Status { get; init; } = GatewayTransactionStatus.Pending;

    /// <summary>
    /// Vendor-assigned transaction identifier (e.g. a Stedi transaction id),
    /// useful for correlating with the external system. Non-PHI.
    /// </summary>
    public string? ExternalTransactionId { get; init; }

    /// <summary>
    /// Cloud Health Office correlation id that ties this transaction to the
    /// originating request/trace across services.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Tenant the transaction was executed on behalf of.</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Wall-clock time the gateway spent on the transaction.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>Number of retries performed before the recorded outcome.</summary>
    public int RetryCount { get; init; }

    /// <summary>Category of failure when <see cref="Status"/> is not successful.</summary>
    public GatewayErrorCategory ErrorCategory { get; init; } = GatewayErrorCategory.None;
}

/// <summary>Lifecycle status of a gateway transaction.</summary>
public enum GatewayTransactionStatus
{
    /// <summary>Created but not yet submitted.</summary>
    Pending,

    /// <summary>Submitted to the gateway; awaiting a response.</summary>
    Submitted,

    /// <summary>Completed successfully with a usable response.</summary>
    Completed,

    /// <summary>The payer/clearinghouse rejected the transaction (business rejection).</summary>
    Rejected,

    /// <summary>The transaction failed before a business response was obtained.</summary>
    Failed,

    /// <summary>The transaction did not complete within the allotted time.</summary>
    TimedOut
}

/// <summary>
/// Coarse error taxonomy for non-successful gateway transactions. Categories
/// are chosen to be actionable (retry vs. fix request vs. escalate) without
/// carrying vendor-specific error codes into the domain.
/// </summary>
public enum GatewayErrorCategory
{
    /// <summary>No error — the transaction succeeded.</summary>
    None,

    /// <summary>The request failed local/gateway validation before transport.</summary>
    Validation,

    /// <summary>Authentication against the gateway failed (e.g. bad/expired API key).</summary>
    Authentication,

    /// <summary>The gateway credential is valid but not authorized for the operation.</summary>
    Authorization,

    /// <summary>Network/connectivity failure reaching the gateway or payer.</summary>
    Connectivity,

    /// <summary>The gateway signalled rate limiting (e.g. HTTP 429).</summary>
    RateLimited,

    /// <summary>The transaction exceeded its time budget.</summary>
    Timeout,

    /// <summary>The gateway or an upstream dependency was temporarily unavailable (e.g. HTTP 5xx).</summary>
    ServiceUnavailable,

    /// <summary>The payer/clearinghouse returned a business-level rejection.</summary>
    PayerRejected,

    /// <summary>The gateway returned a response that could not be parsed into the canonical model.</summary>
    MalformedResponse,

    /// <summary>The gateway is misconfigured (missing/invalid credentials, base URL, environment).</summary>
    Configuration,

    /// <summary>The gateway does not support the requested transaction.</summary>
    NotSupported,

    /// <summary>No payer matched the supplied identifier.</summary>
    PayerNotFound,

    /// <summary>More than one payer matched; the caller must disambiguate.</summary>
    AmbiguousPayer,

    /// <summary>The canonical payer has no identifier for the selected clearinghouse.</summary>
    ExternalIdentifierMissing,

    /// <summary>The payer supports the transaction only after enrollment, which is not complete.</summary>
    EnrollmentRequired,

    /// <summary>Local payer reference data could not be read.</summary>
    ReferenceDataUnavailable,

    /// <summary>An unexpected internal error occurred.</summary>
    Internal,

    /// <summary>The same claim version was already transmitted through this gateway.</summary>
    DuplicateSubmission,

    /// <summary>The claim type cannot be represented from the available CHO data.</summary>
    ClaimTypeNotReady,

    /// <summary>A 277CA could not be matched uniquely to a durable transmission.</summary>
    UnableToMatchTransmission,

    /// <summary>The discovered transaction is not a supported acknowledgment (e.g. 835).</summary>
    UnsupportedAcknowledgment,

    /// <summary>Attachment bytes were not found in the secure content store.</summary>
    AttachmentNotFound,

    /// <summary>The referenced claim transmission does not exist.</summary>
    TransmissionNotFound,

    /// <summary>Claim, tenant, or payer identifiers do not match the transmission.</summary>
    ClaimMismatch,

    /// <summary>The service line was not present on the original submitted claim.</summary>
    ServiceLineNotFound,

    /// <summary>The attachment MIME type is not in the gateway allow-list.</summary>
    UnsupportedContentType,

    /// <summary>The attachment exceeds CHO or vendor size limits.</summary>
    AttachmentTooLarge,

    /// <summary>The secure content store could not be reached.</summary>
    StorageUnavailable,

    /// <summary>The gateway rejected the attachment after transport.</summary>
    GatewayRejected,

    /// <summary>Content scanning marked the attachment unsafe or quarantined.</summary>
    AttachmentUnsafe,

    /// <summary>Inbound transaction could not be parsed into the canonical model.</summary>
    MalformedTransaction,

    /// <summary>The inbound payer/trading-partner identifier is missing or unknown.</summary>
    InvalidPayer,

    /// <summary>No payer-side claim matched the supplied identifiers.</summary>
    ClaimNotFound,

    /// <summary>More than one payer-side claim matched; the caller must disambiguate.</summary>
    AmbiguousClaim,

    /// <summary>More than one service line matched a line-level attachment.</summary>
    AmbiguousServiceLine,

    /// <summary>Transport checksum did not match stored content.</summary>
    ChecksumMismatch,

    /// <summary>Identical inbound attachment was already accepted.</summary>
    DuplicateAttachment,

    /// <summary>Inbound attachment could not be matched uniquely to a payer-side claim.</summary>
    UnableToMatch,

    /// <summary>The payer could not return claim status for an otherwise valid inquiry.</summary>
    ClaimStatusUnavailable
}
