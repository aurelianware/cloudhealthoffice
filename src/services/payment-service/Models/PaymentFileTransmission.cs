using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace PaymentService.Models;

/// <summary>
/// Where a payment run's NACHA file stands on its way to the bank.
/// <code>
///   Pending ──claim──▶ Transmitting ──upload+rename confirmed──▶ Transmitted
///                          │  └──definitely not delivered──▶ Failed ──retry──▶ Transmitting
///                          └──outcome unknown / lease expired──▶ NeedsReview
///   NeedsReview ──file found in the drop (reconcile)──▶ Transmitted
///   NeedsReview ──second user records the bank's answer──▶ Transmitted | Failed
/// </code>
/// A file is never sent from <see cref="NeedsReview"/> or <see cref="Transmitted"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentFileTransmissionStatus
{
    /// <summary>Approved for transmission; no attempt has started.</summary>
    Pending,

    /// <summary>An attempt holds the record (until <see cref="PaymentFileTransmission.LeaseUntil"/>).</summary>
    Transmitting,

    /// <summary>The bank has the file (see <see cref="PaymentFileTransmission.ConfirmedBy"/>). Final.</summary>
    Transmitted,

    /// <summary>The last attempt definitely did not deliver the file (nothing reached the bank's drop). May be retried.</summary>
    Failed,

    /// <summary>
    /// The file may or may not be at the bank. Never re-sent from here: reconcile
    /// against the drop's listing, or a second user records what the bank says.
    /// </summary>
    NeedsReview,

    /// <summary>
    /// Replaced by a re-dated file (<see cref="PaymentFileTransmission.SupersededByFileReference"/>).
    /// Final: never sent, retried, reconciled or resolved. Kept for audit.
    /// </summary>
    Superseded,
}

/// <summary>How the bank's receipt of a file was established.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentFileDeliveryEvidence
{
    /// <summary>The upload and the rename into place both completed.</summary>
    Upload,

    /// <summary>After an unknown outcome, the file (name and size) was found in the bank's drop.</summary>
    RemoteListing,

    /// <summary>A second user recorded that the bank confirmed receipt.</summary>
    BankConfirmation,
}

/// <summary>
/// The bank's acknowledgement of a transmitted file (ACH file acknowledgement,
/// rejects, returns). Modelled only: ingesting the bank's ack/return files is a
/// follow-up (docs/operations/NACHA-BANK-TRANSMISSION-RUNBOOK.md).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentFileAcknowledgementStatus
{
    /// <summary>Not transmitted yet.</summary>
    NotApplicable,

    /// <summary>Transmitted; no acknowledgement recorded.</summary>
    Awaiting,

    /// <summary>The bank accepted the file.</summary>
    Accepted,

    /// <summary>The bank rejected the whole file: nothing was paid from it.</summary>
    Rejected,
}

/// <summary>
/// One NACHA file's transmission to the bank, one record per file
/// (<see cref="KeyFor"/>: tenant + file reference), pinned to the file's
/// SHA-256 at approval. Every attempt, refusal and resolution is appended to
/// <see cref="Attempts"/> in the same conditional write as the state change.
/// Holds no file content, no routing, account or tax id number and no PHI.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class PaymentFileTransmission
{
    [BsonId]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("paymentRunId")] public string PaymentRunId { get; set; } = string.Empty;
    [JsonPropertyName("paymentRunNumber")] public string PaymentRunNumber { get; set; } = string.Empty;

    /// <summary>The run's EFT file reference (FFS-{run number}).</summary>
    [JsonPropertyName("fileReference")] public string FileReference { get; set; } = string.Empty;

    /// <summary>The name the bank receives the file under.</summary>
    [JsonPropertyName("fileName")] public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the exact bytes approved. Every attempt regenerates the file
    /// and refuses to send unless its bytes hash to this value.
    /// </summary>
    [JsonPropertyName("approvedSha256")] public string ApprovedSha256 { get; set; } = string.Empty;

    [JsonPropertyName("byteSize")] public long ByteSize { get; set; }
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
    [JsonPropertyName("totalCreditAmount")] public decimal TotalCreditAmount { get; set; }
    [JsonPropertyName("totalDebitAmount")] public decimal TotalDebitAmount { get; set; }
    [JsonPropertyName("effectiveEntryDate")] public DateTime EffectiveEntryDate { get; set; }

    /// <summary>The run's maker (never the approver).</summary>
    [JsonPropertyName("runCreatedBy")] public string? RunCreatedBy { get; set; }

    /// <summary>The run's executor (the checker), who may not approve its transmission either.</summary>
    [JsonPropertyName("runExecutedBy")] public string? RunExecutedBy { get; set; }

    /// <summary>
    /// The payments the approved file pays (sorted). Every attempt checks that the
    /// file still pays exactly these, and that none was reversed or reissued since.
    /// </summary>
    [JsonPropertyName("approvedPaymentIds")] public List<string> ApprovedPaymentIds { get; set; } = new();

    /// <summary>
    /// For a re-dated file: the run's issued 835s whose BPR16 is not this file's
    /// effective entry date (their providers must be told). Copied from the pinned file.
    /// </summary>
    [JsonPropertyName("remittanceDateNotices")] public List<RemittanceDateNotice> RemittanceDateNotices { get; set; } = new();

    /// <summary>The user who approved sending this file to the bank (token subject).</summary>
    [JsonPropertyName("approvedBy")] public string ApprovedBy { get; set; } = string.Empty;
    [JsonPropertyName("approvedAt")] public DateTime ApprovedAt { get; set; }

    [JsonPropertyName("status")] public PaymentFileTransmissionStatus Status { get; set; } = PaymentFileTransmissionStatus.Pending;

    /// <summary>Transmission attempts started (uploads tried), not counting refusals or reconciliations.</summary>
    [JsonPropertyName("attemptCount")] public int AttemptCount { get; set; }

    /// <summary>While <see cref="PaymentFileTransmissionStatus.Transmitting"/>: when the attempt is presumed dead (then NeedsReview, never re-sent).</summary>
    [JsonPropertyName("leaseUntil")] public DateTime? LeaseUntil { get; set; }

    /// <summary>Why the record is Failed or NeedsReview (safe to show: never a credential or file content).</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    [JsonPropertyName("remoteFileName")] public string? RemoteFileName { get; set; }

    /// <summary>Host and directory of the bank's drop (never credentials).</summary>
    [JsonPropertyName("destination")] public string? Destination { get; set; }

    [JsonPropertyName("transmittedAt")] public DateTime? TransmittedAt { get; set; }
    [JsonPropertyName("transmittedBy")] public string? TransmittedBy { get; set; }
    [JsonPropertyName("confirmedBy")] public PaymentFileDeliveryEvidence? ConfirmedBy { get; set; }

    /// <summary>When Superseded: the re-dated file that replaced this one.</summary>
    [JsonPropertyName("supersededByFileReference")] public string? SupersededByFileReference { get; set; }
    [JsonPropertyName("supersededAt")] public DateTime? SupersededAt { get; set; }

    [JsonPropertyName("acknowledgement")] public PaymentFileAcknowledgementStatus Acknowledgement { get; set; } = PaymentFileAcknowledgementStatus.NotApplicable;
    [JsonPropertyName("acknowledgedAt")] public DateTime? AcknowledgedAt { get; set; }
    [JsonPropertyName("acknowledgementReference")] public string? AcknowledgementReference { get; set; }

    /// <summary>Append-only audit of every attempt, refusal, reconciliation and resolution.</summary>
    [JsonPropertyName("attempts")] public List<PaymentFileTransmissionAttempt> Attempts { get; set; } = new();

    /// <summary>
    /// Transactional outbox: events written in the same conditional write as
    /// the state change they describe (the appeals-service outbox pattern).
    /// <see cref="PaymentFileOutboxMessage.Type"/> PaymentFileTransmitted is the
    /// seam GL posting consumes; no dispatcher publishes them yet.
    /// </summary>
    [JsonPropertyName("outbox")] public List<PaymentFileOutboxMessage> Outbox { get; set; } = new();

    /// <summary>Optimistic concurrency token: every write requires the version read and sets a new one.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }

    /// <summary>Unique per (tenant, file reference). Characters Cosmos forbids in ids are escaped.</summary>
    public static string KeyFor(string tenantId, string fileReference)
        => $"nacha:{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(fileReference)}";
}

/// <summary>What one audited action on a transmission did.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentFileTransmissionAction
{
    /// <summary>An upload was attempted.</summary>
    Transmit,

    /// <summary>A request was refused before anything was sent (hash mismatch, wrong state).</summary>
    Refused,

    /// <summary>A Transmitting attempt outlived its lease: its outcome is unknown.</summary>
    LeaseExpired,

    /// <summary>The bank's drop was listed for the file.</summary>
    Reconcile,

    /// <summary>A user recorded what the bank said about a NeedsReview file.</summary>
    Resolve,

    /// <summary>
    /// An attempt finished after its record had changed (its lease expired and the
    /// record moved on). Its outcome is evidence: unless the record is already
    /// Transmitted, it is forced back to NeedsReview.
    /// </summary>
    LateOutcome,

    /// <summary>The file was replaced by a re-dated one; this record is final.</summary>
    Superseded,
}

/// <summary>One audited action: operator, file hash, result. Never file content or bank numbers.</summary>
public sealed class PaymentFileTransmissionAttempt
{
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("action")] public PaymentFileTransmissionAction Action { get; set; }

    /// <summary>The acting user (token subject), or "payment-service" for a system action.</summary>
    [JsonPropertyName("by")] public string By { get; set; } = string.Empty;
    [JsonPropertyName("at")] public DateTime At { get; set; }

    /// <summary>The SHA-256 of the file this action saw (the regenerated bytes for a Transmit or Refused).</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;

    /// <summary>The record's status after the action.</summary>
    [JsonPropertyName("result")] public PaymentFileTransmissionStatus Result { get; set; }

    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

/// <summary>
/// One event in an outbox: a transmission record's <c>Outbox</c>, or a payment or
/// reversal run's <c>GlOutbox</c>. Written in the same document write as the state
/// change it describes; <c>GlEventDispatcher</c> delivers it and sets
/// <see cref="PublishedAt"/>. Delivery is at least once: the consumer de-duplicates
/// on <see cref="EventId"/>.
/// </summary>
public sealed class PaymentFileOutboxMessage
{
    public const string TransmittedType = CloudHealthOffice.Finance.Contracts.GlEventTypes.PaymentFileTransmitted;

    /// <summary>Deterministic per (tenant, source, type): consumers de-duplicate on it.</summary>
    [JsonPropertyName("eventId")] public string EventId { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    /// <summary>camelCase JSON of the CloudHealthOffice.Finance.Contracts payload for <see cref="Type"/>.</summary>
    [JsonPropertyName("payloadJson")] public string PayloadJson { get; set; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }

    /// <summary>Null until the dispatcher delivered it (the consumer acknowledged).</summary>
    [JsonPropertyName("publishedAt")] public DateTime? PublishedAt { get; set; }

    /// <summary>Failed delivery attempts; the dispatcher backs off on them, never drops the event.</summary>
    [JsonPropertyName("publishAttempts")] public int PublishAttempts { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("nextAttemptAt")] public DateTime? NextAttemptAt { get; set; }
}

/// <summary>Body recording what the bank said about a NeedsReview file.</summary>
public sealed class ResolvePaymentFileTransmissionRequest
{
    /// <summary>True: the bank confirmed it has the file. False: the bank confirmed it does not.</summary>
    [JsonPropertyName("bankReceived")] public bool? BankReceived { get; set; }

    /// <summary>The evidence: who at the bank, when, their reference.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
