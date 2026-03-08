using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RfaiService.Models;

/// <summary>
/// Status of an RFAI case.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RfaiStatus
{
    Open,
    DocsReceived,
    Closed,
    Cancelled
}

/// <summary>
/// An item requested as part of an RFAI (e.g. operative report, lab result).
/// </summary>
public class RequestedItem
{
    /// <summary>Optional clinical or payer-defined code (e.g. PWK qualifier).</summary>
    public string? Code { get; set; }

    /// <summary>Human-readable description of the requested document.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether this item is required to close the RFAI.</summary>
    public bool? Required { get; set; }
}

/// <summary>
/// Source transaction that delivered an attachment (e.g. 275).
/// </summary>
public class SourceTransaction
{
    /// <summary>Transaction set identifier, e.g. "275".</summary>
    public string? TransactionSetId { get; set; }

    /// <summary>GS control number from the interchange.</summary>
    public string? GsControl { get; set; }

    /// <summary>ST control number from the transaction set.</summary>
    public string? StControl { get; set; }
}

/// <summary>
/// A received attachment record appended when an inbound 275 arrives.
/// </summary>
public class ReceivedAttachment
{
    /// <summary>When the attachment was received (default: UtcNow).</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>X12 275 TRN02 or payer-assigned attachment control number.</summary>
    public string? AttachmentControlNumber { get; set; }

    /// <summary>Storage provider identifier (e.g. "azure-blob", "s3").</summary>
    public string? StorageProvider { get; set; }

    /// <summary>Storage key / path within the provider.</summary>
    public string? StorageKey { get; set; }

    /// <summary>SHA-256 or other hash of the file content.</summary>
    public string? FileHash { get; set; }

    /// <summary>Source EDI transaction metadata.</summary>
    public SourceTransaction? SourceTransaction { get; set; }
}

/// <summary>
/// An RFAI (Request for Additional Information) case.
/// Created when a payer needs clinical attachments to adjudicate a prior-auth or claim.
/// </summary>
public class RfaiCase
{
    /// <summary>Unique identifier (MongoDB _id).</summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Multi-tenant partition key.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Authorization number from 278 TRN02 or payer system.</summary>
    public string AuthNumber { get; set; } = string.Empty;

    /// <summary>Current lifecycle status.</summary>
    public RfaiStatus Status { get; set; } = RfaiStatus.Open;

    /// <summary>Clinical documents or data items being requested.</summary>
    public List<RequestedItem> RequestedItems { get; set; } = new();

    /// <summary>Attachments received in response to this RFAI.</summary>
    public List<ReceivedAttachment> ReceivedAttachments { get; set; } = new();

    /// <summary>Optional deadline for receiving attachments.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Freeform notes.</summary>
    public string? Notes { get; set; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC last-updated timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
