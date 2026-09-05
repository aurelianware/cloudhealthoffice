namespace FhirService.Models.PayerToPayer;

/// <summary>
/// An outbound Payer-to-Payer exchange request (CMS-0057-F P2P-02): Cloud Health
/// Office — the new/current payer — asks another (prior) payer for a
/// transitioning member's data.
///
/// Everything a caller may supply is a *reference*, never an authorization and
/// never a network location:
///   * the member is one of CHO's own members (tenant-scoped);
///   * the target payer is named by id and resolved against the trusted payer
///     directory (<c>IPayerToPayerEndpointResolver</c>) — a caller can never
///     supply a URL for CHO to call (SSRF);
///   * the member's opt-in is decided server-side by
///     <c>IPayerToPayerConsentGate</c>, never accepted from the request. That
///     gate uses the generic active opt-in signal and does not introduce a
///     dedicated Payer-to-Payer ConsentType — P2P-03 stays PARTIAL and
///     independent.
/// </summary>
public sealed class PayerToPayerOutboundRequest
{
    /// <summary>Tenant the exchange is scoped to (isolation boundary).</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>CHO member the exchange is for — a member of <see cref="TenantId"/>.</summary>
    public string MemberId { get; init; } = string.Empty;

    /// <summary>
    /// Id of the payer to initiate against (typically the member's prior payer).
    /// Resolved against the configured payer directory; an unknown id fails
    /// closed with <see cref="PayerToPayerOutboundFailure.TargetPayerNotConfigured"/>.
    /// </summary>
    public string TargetPayerId { get; init; } = string.Empty;

    /// <summary>Who/what initiated the exchange — audit context.</summary>
    public string? InitiatedBy { get; init; }

    /// <summary>
    /// Stable key of the coverage transition that triggered the exchange (e.g. an
    /// enrollment/transition id). Together with tenant + member + target payer it
    /// forms the idempotency key, so a retried initiation resumes the same
    /// exchange instead of opening a second one.
    /// </summary>
    public string? TransitionKey { get; init; }

    /// <summary>
    /// Point in time the prior coverage context is requested "as of" (yyyy-MM-dd).
    /// Used to pick the member's coverage with the target payer when several
    /// exist. Defaults to the exchange date.
    /// </summary>
    public string? AsOfDate { get; init; }

    /// <summary>Exchange date; anchors the requested lookback window.</summary>
    public DateTime ExchangeDateUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Claims lookback requested from the prior payer (locked P2P rule: 5 years).</summary>
    public int LookbackYears { get; init; } = 5;
}

/// <summary>
/// Lifecycle state of an outbound exchange. Progress states (<see cref="Pending"/>,
/// <see cref="Matching"/>, <see cref="Matched"/>, <see cref="RequestingData"/>)
/// are recorded as the workflow advances; the rest are terminal.
/// </summary>
public enum PayerToPayerOutboundStatus
{
    /// <summary>Accepted and recorded; no remote call made yet.</summary>
    Pending,

    /// <summary>The remote <c>$member-match</c> has been issued.</summary>
    Matching,

    /// <summary>The remote payer resolved the member; data has not been requested yet.</summary>
    Matched,

    /// <summary>The remote member-data export has been issued.</summary>
    RequestingData,

    /// <summary>A validated member-scoped package was received.</summary>
    Completed,

    /// <summary>The remote payer did not resolve the member — no data was requested.</summary>
    NoMatch,

    /// <summary>The remote payer resolved more than one member — no data was requested.</summary>
    Ambiguous,

    /// <summary>The member has no active opt-in on record — nothing was sent to the remote payer.</summary>
    NotAuthorized,

    /// <summary>The exchange failed; see <see cref="PayerToPayerOutboundExchange.Failure"/>.</summary>
    Failed,
}

/// <summary>
/// Structured failure category for an outbound exchange. Deliberately a closed
/// set rather than free-form text or a raw exception, so callers, audit, and the
/// API surface all branch on the same values and no remote response detail leaks
/// through an error string.
/// </summary>
public enum PayerToPayerOutboundFailure
{
    /// <summary>No failure (the exchange completed, or is still in progress).</summary>
    None,

    /// <summary>The request targeted a tenant this instance does not serve.</summary>
    TenantMismatch,

    /// <summary>The member is not a member of the serving tenant.</summary>
    MemberNotFound,

    /// <summary>The target payer is not in the trusted payer directory for this tenant.</summary>
    TargetPayerNotConfigured,

    /// <summary>The member has no active opt-in for a Payer-to-Payer exchange.</summary>
    NotAuthorized,

    /// <summary>
    /// The member holds several coverages with the target payer and none could be
    /// chosen — refuse rather than assert the wrong prior relationship.
    /// </summary>
    LocalCoverageAmbiguous,

    /// <summary>The remote payer resolved no member from the identity CHO supplied.</summary>
    MemberNoMatch,

    /// <summary>The remote payer resolved more than one member.</summary>
    MemberAmbiguous,

    /// <summary>The remote payer rejected CHO's credentials / authorization.</summary>
    RemoteUnauthorized,

    /// <summary>The remote payer could not be reached, timed out, or returned a server error.</summary>
    RemoteUnavailable,

    /// <summary>The remote payer's response was unparseable, not a Bundle, or not member-consistent.</summary>
    InvalidRemoteResponse,
}

/// <summary>
/// Durable state of one outbound exchange. This is the record the workflow
/// advances and the audit trail keys on — status is never encoded only in a free
/// text field, and no member demographics or payload content are retained here.
/// </summary>
public sealed class PayerToPayerOutboundExchange
{
    /// <summary>Stable exchange id (correlates audit, retries, and the received package).</summary>
    public string ExchangeId { get; init; } = string.Empty;

    public string TenantId { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;

    /// <summary>The CHO coverage that established the member's relationship with the target payer, when known.</summary>
    public string? LocalCoverageId { get; set; }

    public string TargetPayerId { get; init; } = string.Empty;

    /// <summary>
    /// Directory key of the endpoint used — an opaque, config-owned identifier.
    /// The endpoint URL is deliberately NOT stored on the exchange or written to
    /// logs.
    /// </summary>
    public string? TargetEndpointKey { get; set; }

    /// <summary>tenant | member | target payer | transition key.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    public PayerToPayerOutboundStatus Status { get; set; } = PayerToPayerOutboundStatus.Pending;
    public PayerToPayerOutboundFailure Failure { get; set; } = PayerToPayerOutboundFailure.None;

    /// <summary>Outcome of the remote member-match step, once it has run.</summary>
    public string? MemberMatchOutcome { get; set; }

    /// <summary>Outcome of the remote export step, once it has run.</summary>
    public string? ExportOutcome { get; set; }

    /// <summary>Member id the remote payer resolved (their identifier, not CHO's).</summary>
    public string? RemoteMemberId { get; set; }

    /// <summary>Number of FHIR resources in the received package (0 until completed).</summary>
    public int ReceivedResourceCount { get; set; }

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsTerminal => Status is PayerToPayerOutboundStatus.Completed
        or PayerToPayerOutboundStatus.NoMatch
        or PayerToPayerOutboundStatus.Ambiguous
        or PayerToPayerOutboundStatus.NotAuthorized
        or PayerToPayerOutboundStatus.Failed;
}

/// <summary>
/// Where a received package came from. Retained so data obtained from another
/// payer is never mistaken for CHO-originated data. The remote endpoint is
/// identified by its directory key — never by URL, and never with credentials.
/// </summary>
public sealed class PayerToPayerSourceProvenance
{
    public string SourcePayerId { get; init; } = string.Empty;
    public string SourceEndpointKey { get; init; } = string.Empty;
    public string ExchangeId { get; init; } = string.Empty;
    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A validated, member-scoped package received from a remote payer, with its
/// provenance. The Bundle is the remote payer's own FHIR payload, parsed and
/// checked for member consistency — it is NOT written into CHO's member record
/// by this workflow (durable ingestion is separate follow-up work).
/// </summary>
public sealed class PayerToPayerReceivedPackage
{
    public Hl7.Fhir.Model.Bundle Bundle { get; init; } = new();

    /// <summary>Member id as the remote payer knows them.</summary>
    public string RemoteMemberId { get; init; } = string.Empty;

    /// <summary>Number of resources in the package (excluding the Provenance stamp CHO adds).</summary>
    public int ResourceCount { get; init; }

    public PayerToPayerSourceProvenance Provenance { get; init; } = new();
}

/// <summary>Result of an outbound initiation: the exchange state plus, on success, the package.</summary>
public sealed class PayerToPayerOutboundResult
{
    public PayerToPayerOutboundExchange Exchange { get; init; } = new();

    /// <summary>The received package — only when <see cref="PayerToPayerOutboundStatus.Completed"/>.</summary>
    public PayerToPayerReceivedPackage? Package { get; init; }

    /// <summary>True when this call replayed an existing exchange instead of initiating a new one.</summary>
    public bool IsReplay { get; init; }

    public PayerToPayerOutboundAuditEntry Audit { get; init; } = new();

    public bool Succeeded => Exchange.Status == PayerToPayerOutboundStatus.Completed;
}

/// <summary>
/// Auditable record of an outbound exchange — tenant, member, target payer,
/// exchange id, outcome, resource count, and when. Carries no member
/// demographics, no payload, no endpoint URL, and no credentials.
/// </summary>
public sealed class PayerToPayerOutboundAuditEntry
{
    public string TenantId { get; init; } = string.Empty;
    public string MemberId { get; init; } = string.Empty;
    public string TargetPayerId { get; init; } = string.Empty;
    public string? TargetEndpointKey { get; init; }
    public string ExchangeId { get; init; } = string.Empty;
    public string? InitiatedBy { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string FailureCategory { get; init; } = PayerToPayerOutboundFailure.None.ToString();
    public int ResourceCount { get; init; }
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}
