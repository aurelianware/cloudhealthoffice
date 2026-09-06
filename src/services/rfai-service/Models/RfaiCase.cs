using System.Text.Json.Serialization;

namespace RfaiService.Models;

/// <summary>
/// Request for Additional Information case — the durable record of ONE
/// additional-information cycle on ONE prior authorization.
///
/// This is the single additional-information aggregate in Cloud Health Office.
/// The Da Vinci CDex surface in fhir-service (a Task on the CDex Task
/// Attachment Request profile, plus the <c>$submit-attachment</c> operation) is
/// a PROJECTION of this record, not a second store: the standards-facing
/// representation and the internal case are the same row.
///
/// Correlation chain:
/// <code>
///   Tenant → Authorization (AuthNumber / AuthorizationId)
///          → RfaiCase (Id, TrackingId, Sequence)
///              → ReceivedAttachment (SubmissionId)
///                  → stored artifact (StorageProvider + StorageKey + FileHash)
/// </code>
///
/// LIFECYCLE. The four states below are CHO's own and are unchanged; the
/// conceptual CDex/RFAI lifecycle maps onto them exactly:
/// <code>
///   Requested / AwaitingResponse       → Open
///   ResponseReceived / AcceptedForReview → DocsReceived
///   Closed                             → Closed
///   Cancelled                          → Cancelled
/// </code>
/// An INVALID response is not a state: it is refused at intake and the case
/// stays <see cref="RfaiStatus.Open"/>, so a rejected submission can never
/// consume the provider's one chance to answer. Expiry is DERIVED from
/// <see cref="DueDate"/> (see <see cref="IsOverdue"/>) rather than stored,
/// because nothing in this repository sweeps due dates — a stored "Expired"
/// nothing ever sets would be a lie in the data.
///
/// The prior-authorization lifecycle is NOT duplicated here. The PA's own status
/// (Pended → InReview) lives on authorization-service's <c>Authorization</c> and
/// moves in response to this case's transitions, never instead of them.
/// </summary>
public class RfaiCase
{
    /// <summary>
    /// Cosmos DB / MongoDB document ID.
    ///
    /// DETERMINISTIC for cases created through
    /// <see cref="Services.RfaiCaseFactory"/>: it is derived from the caller's
    /// correlation key, so the primary key itself — not an application-level
    /// read-then-write — is what makes creation idempotent. Two workers
    /// processing the same A4 review decision insert the same id and exactly one
    /// of them wins.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Multi-tenant partition key. Always from authenticated context, never from a payload.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Authorization number from the originating 278 transaction (TRN02).
    /// Alphanumeric; used to correlate inbound 275 attachments and CDex
    /// submissions that name the authorization in <c>AttachTo</c>.
    /// </summary>
    public string AuthNumber { get; set; } = string.Empty;

    /// <summary>
    /// Internal authorization document id, when the creating caller knew it.
    /// Kept alongside <see cref="AuthNumber"/> so the chain survives a payer
    /// re-issuing an authorization number.
    /// </summary>
    public string? AuthorizationId { get; set; }

    /// <summary>
    /// The externally-facing handle for this request — the attachment control
    /// number a provider quotes when submitting documentation (CDex
    /// <c>$submit-attachment</c> <c>TrackingId</c>; X12 275 ACN).
    ///
    /// RANDOM, not derived: it is one of the keys an intake must match, so it
    /// must not be computable from facts a caller already knows.
    /// </summary>
    public string TrackingId { get; set; } = string.Empty;

    /// <summary>
    /// The caller-supplied idempotency key for the event that created this case
    /// (typically the A4 review decision). Retained so a replay can be
    /// recognised as a replay rather than inferred from timestamps.
    /// </summary>
    public string? CorrelationKey { get; set; }

    /// <summary>
    /// 1-based cycle number for this authorization. A second RFAI cycle is a new
    /// record with the next sequence — it never overwrites the first.
    /// </summary>
    public int Sequence { get; set; } = 1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RfaiStatus Status { get; set; } = RfaiStatus.Open;

    /// <summary>Items the payer is requesting (clinical notes, images, lab results, etc.).</summary>
    public List<RequestedItem> RequestedItems { get; set; } = new();

    /// <summary>Attachments received in response to this RFAI.</summary>
    public List<ReceivedAttachment> ReceivedAttachments { get; set; } = new();

    /// <summary>Date/time by which the payer expects the attachments.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// Free text SUPPLEMENTING the coded <see cref="RequestedItems"/> — never
    /// replacing them. A request with only notes and no requested items is
    /// refused at creation.
    /// </summary>
    public string? Notes { get; set; }

    // ── Correlation context ──────────────────────────────────────────────────
    // The minimum needed to bind a response to the right authorization and to
    // refuse one from an unrelated provider. Deliberately NOT patient name, date
    // of birth, diagnosis text or any other demographic: the authorization
    // record already holds those and duplicating them here would spread PHI for
    // no correlation benefit.

    /// <summary>Member the authorization is for, as the authorization records it.</summary>
    public string? MemberId { get; set; }

    /// <summary>Requesting provider NPI — the party expected to answer.</summary>
    public string? RequestingProviderNpi { get; set; }

    /// <summary>X12 278 review decision that caused the request. Expected to be "A4".</summary>
    public string? ReviewDecision { get; set; }

    /// <summary>Coded reason the information is needed, where the reviewer supplied one.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>Human-readable reason, supplementing <see cref="ReasonCode"/>.</summary>
    public string? ReasonDescription { get; set; }

    // ── Provenance ───────────────────────────────────────────────────────────

    /// <summary>Who or what created the request (reviewer id, or the service principal).</summary>
    public string? RequestedBy { get; set; }

    /// <summary>
    /// Which path created it — see <see cref="RfaiRequestSources"/>. Records WHY
    /// the request exists, so a documentation request is never mistaken for one
    /// inferred from a generic pended state.
    /// </summary>
    public string RequestSource { get; set; } = RfaiRequestSources.Unknown;

    /// <summary>First time the request was handed to the provider/system.</summary>
    public DateTime? FirstDeliveredAt { get; set; }

    /// <summary>Most recent delivery.</summary>
    public DateTime? LastDeliveredAt { get; set; }

    /// <summary>How many times the request has been retrieved. Provenance only; never a limit.</summary>
    public int DeliveryCount { get; set; }

    /// <summary>When the first valid response was accepted.</summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>Closed/cancelled provenance.</summary>
    public string? ClosedBy { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string? ClosureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Derived ──────────────────────────────────────────────────────────────

    /// <summary>
    /// True while the payer is still waiting on the provider. The one predicate
    /// intake, projection and "is there already an open cycle?" all read, so
    /// they cannot drift apart.
    /// </summary>
    [JsonIgnore]
    public bool IsOpen => Status == RfaiStatus.Open;

    /// <summary>
    /// Past its due date and still open. DERIVED — nothing in this repository
    /// sweeps due dates, so expiry is reported, not recorded.
    /// </summary>
    public bool IsOverdue(DateTime asOfUtc)
        => IsOpen && DueDate.HasValue && DueDate.Value < asOfUtc;
}

/// <summary>
/// A single item the payer is requesting as part of the RFAI.
///
/// STRUCTURED FIRST. <see cref="Description"/> is required because a human has
/// to be able to read the request, but it supplements the codes rather than
/// standing in for them: the X12 PWK code and the LOINC attachment code are what
/// a receiving system acts on, and the service-line / diagnosis context is what
/// tells the provider WHICH part of the request is short of documentation.
/// </summary>
public class RequestedItem
{
    /// <summary>
    /// PWK/attachment type code from X12 (e.g. "03"=Report of Tests/Analysis,
    /// "AS"=Admission Summary, "B2"=Prescription, "OZ"=Support Data for Claim).
    /// Optional — description is always required.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// LOINC attachment/document type code, as CDex and the HL7 Attachments IG
    /// use for <c>Task.input</c> and <c>DocumentReference.type</c>
    /// (e.g. "18842-5" Discharge summary). Carried alongside the X12 code rather
    /// than instead of it: the same request has to be expressible on both wires.
    /// </summary>
    public string? LoincCode { get; set; }

    /// <summary>Human-readable description of the requested document.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether this item is mandatory for the auth decision.</summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// The requested service line this item is about (HCPCS/CPT), when the
    /// reviewer's question is about one line rather than the whole request.
    /// </summary>
    public string? ServiceLineProcedureCode { get; set; }

    /// <summary>Diagnosis context (ICD-10) for the question, where the reviewer gave one.</summary>
    public string? DiagnosisCode { get; set; }
}

/// <summary>
/// Record of an artifact received in response to this RFAI.
///
/// Content is NOT held here. The bytes live in the platform document store and
/// this row keeps the pointer (<see cref="StorageProvider"/> +
/// <see cref="StorageKey"/>) and the integrity hash, so clinical content is
/// never duplicated into the case document, an audit event, or a log line.
/// </summary>
public class ReceivedAttachment
{
    /// <summary>
    /// Stable identity of ONE submission, computed by the intake path from
    /// tenant, case and content. A replay of the same submission carries the same
    /// value and is recognised as a duplicate instead of appending a second row.
    /// </summary>
    public string? SubmissionId { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>275 ACN (Attachment Control Number) or attachment-service document ID.</summary>
    public string? AttachmentControlNumber { get; set; }

    /// <summary>Storage backend (e.g. "azure-blob", "s3").</summary>
    public string? StorageProvider { get; set; }

    /// <summary>Blob/object key within the storage provider. Always server-derived.</summary>
    public string? StorageKey { get; set; }

    /// <summary>SHA-256 hex hash of the attachment bytes for integrity verification.</summary>
    public string? FileHash { get; set; }

    /// <summary>Validated MIME type of the stored artifact.</summary>
    public string? ContentType { get; set; }

    /// <summary>Size in bytes, as measured on the way in.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Caller-supplied title, sanitized. Never used as a storage path.</summary>
    public string? Title { get; set; }

    /// <summary>Document type the submitter says this satisfies (LOINC or X12 PWK).</summary>
    public string? DocumentTypeCode { get; set; }

    /// <summary>Code system for <see cref="DocumentTypeCode"/>.</summary>
    public string? DocumentTypeSystem { get; set; }

    /// <summary>Authenticated caller that submitted it. Never a token or credential.</summary>
    public string? SubmittedBy { get; set; }

    /// <summary>How it arrived — see <see cref="RfaiResponseChannels"/>.</summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Source EDI transaction that delivered the attachment.
    /// Populated once 275 correlation is wired in attachment-service.
    /// </summary>
    public SourceTransaction? SourceTransaction { get; set; }
}

/// <summary>
/// Reference to the X12 transaction that carried the attachment (typically a 275).
/// </summary>
public class SourceTransaction
{
    /// <summary>X12 transaction set ID (e.g. "275").</summary>
    public string TransactionSetId { get; set; } = string.Empty;

    /// <summary>GS06 — functional group control number.</summary>
    public string? GsControl { get; set; }

    /// <summary>ST02 — transaction set control number.</summary>
    public string? StControl { get; set; }
}

public enum RfaiStatus
{
    /// <summary>Requested and awaiting the provider's response.</summary>
    Open,

    /// <summary>A valid response was accepted; the authorization returns to review.</summary>
    DocsReceived,

    /// <summary>The payer is done with this cycle. Retained as history.</summary>
    Closed,

    /// <summary>Withdrawn before a response was required.</summary>
    Cancelled
}

/// <summary>
/// What created an additional-information request. A request must always name
/// the decision that caused it, so a generic pended state can never be mistaken
/// for one.
/// </summary>
public static class RfaiRequestSources
{
    /// <summary>An X12 278 review decision of A4 recorded by authorization-service.</summary>
    public const string ReviewDecisionA4 = "review-decision-a4";

    /// <summary>A payer reviewer raising the request directly.</summary>
    public const string PayerReview = "payer-review";

    /// <summary>Source not stated by the creator.</summary>
    public const string Unknown = "unknown";
}

/// <summary>How a response reached CHO.</summary>
public static class RfaiResponseChannels
{
    /// <summary>Da Vinci CDex <c>$submit-attachment</c> on the FHIR surface.</summary>
    public const string CdexSubmitAttachment = "cdex-submit-attachment";

    /// <summary>X12 275 attachment correlated by attachment-service.</summary>
    public const string X12Attachment275 = "x12-275";
}
