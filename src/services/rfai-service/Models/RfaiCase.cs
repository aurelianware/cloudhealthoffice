using System.Text.Json.Serialization;

namespace RfaiService.Models;

/// <summary>
/// Request for Additional Information case.
///
/// Tracks a payer's request for clinical attachments tied to a prior authorization.
/// The authorization number (authNumber) maps to TRN02 in the 278 transaction.
///
/// Lifecycle:
///   Open → DocsReceived  (when attachment-service posts a received attachment)
///   Open → Closed        (manual close: payer satisfied or time limit passed)
///   Open → Cancelled     (provider or payer cancelled the request)
/// </summary>
public class RfaiCase
{
    /// <summary>Cosmos DB / MongoDB document ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Multi-tenant partition key.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Authorization number from the originating 278 transaction (TRN02).
    /// Alphanumeric; used to correlate inbound 275 attachments.
    /// </summary>
    public string AuthNumber { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RfaiStatus Status { get; set; } = RfaiStatus.Open;

    /// <summary>Items the payer is requesting (clinical notes, images, lab results, etc.).</summary>
    public List<RequestedItem> RequestedItems { get; set; } = new();

    /// <summary>Attachments received in response to this RFAI.</summary>
    public List<ReceivedAttachment> ReceivedAttachments { get; set; } = new();

    /// <summary>Date/time by which the payer expects the attachments.</summary>
    public DateTime? DueDate { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single item the payer is requesting as part of the RFAI.
/// </summary>
public class RequestedItem
{
    /// <summary>
    /// PWK/attachment type code from X12 (e.g. "03"=Report of Tests/Analysis,
    /// "AS"=Admission Summary, "B2"=Prescription, "OZ"=Support Data for Claim).
    /// Optional — description is always required.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>Human-readable description of the requested document.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether this item is mandatory for the auth decision.</summary>
    public bool Required { get; set; } = true;
}

/// <summary>
/// Record of an attachment received in response to this RFAI.
/// Populated by attachment-service via POST /api/rfai/{id}/attachments/received.
/// </summary>
public class ReceivedAttachment
{
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>275 ACN (Attachment Control Number) or attachment-service document ID.</summary>
    public string? AttachmentControlNumber { get; set; }

    /// <summary>Storage backend (e.g. "azure-blob", "s3").</summary>
    public string? StorageProvider { get; set; }

    /// <summary>Blob/object key within the storage provider.</summary>
    public string? StorageKey { get; set; }

    /// <summary>SHA-256 hex hash of the attachment bytes for integrity verification.</summary>
    public string? FileHash { get; set; }

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
    Open,
    DocsReceived,
    Closed,
    Cancelled
}
