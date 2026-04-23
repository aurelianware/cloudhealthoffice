using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace AppealsService.Models;

/// <summary>
/// Append-only audit row for <see cref="Appeal"/>. One row per lifecycle
/// action (created, status-changed, closed, note-added, attachment-added,
/// attachment-acknowledged, overdue-observed, assigned, migrated from
/// pre-modernization status values). Partition key is
/// <c>{tenantId}:{appealId}</c> so the full audit trail for an appeal lives
/// in a single partition and scans cheaply.
/// </summary>
[BsonIgnoreExtraElements]
public class AppealEvent
{
    /// <summary>Cosmos document id + Mongo `_id`. Defaults to <see cref="EventId"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Cosmos partition key. Derived via <see cref="BuildPartitionKey"/>.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string AppealId { get; set; } = string.Empty;

    /// <summary>
    /// Client-supplied idempotency key. Duplicate appends with the same
    /// <c>EventId</c> are silently ignored at the repository layer.
    /// </summary>
    [Required]
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public AppealEventType EventType { get; set; }

    public AppealStatus? FromStatus { get; set; }
    public AppealStatus? ToStatus { get; set; }

    [Required]
    [StringLength(200)]
    public string ActorId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? CorrelationId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Structured, non-PHI payload. Free-form JSON so new event types can
    /// carry whatever context they need without a model change — keep PHI-
    /// adjacent fields OUT of this payload (those live encrypted on the
    /// <see cref="Appeal"/> aggregate). Field-whitelist tests in
    /// AppealsService.Tests enforce the invariant.
    /// </summary>
    [BsonIgnore]
    public JsonObject? Payload { get; set; }

    /// <summary>
    /// Mongo-facing mirror of <see cref="Payload"/>. Not emitted by
    /// System.Text.Json (Cosmos path) — used only by the BSON driver.
    /// </summary>
    [JsonIgnore]
    public string? PayloadJson
    {
        get => Payload?.ToJsonString();
        set => Payload = string.IsNullOrEmpty(value)
            ? null
            : JsonNode.Parse(value) as JsonObject;
    }

    public static string BuildPartitionKey(string tenantId, string appealId) =>
        $"{tenantId}:{appealId}";
}

public enum AppealEventType
{
    AppealCreated = 1,
    AppealStatusChanged = 2,
    AppealClosed = 3,
    AppealNoteAdded = 4,
    AppealAttachmentAdded = 5,
    AppealAttachmentAcknowledged = 6,
    AppealOverdueObserved = 7,
    AppealAssigned = 8,
    /// <summary>
    /// Emitted by <c>HostedServices.AppealStatusMigrationHostedService</c>
    /// for records that carried pre-modernization terminal status values
    /// (Approved/Denied/PartialApproval/Withdrawn) and were rewritten to
    /// <c>Status=Closed</c> + <see cref="Appeal.ClosureReasonCode"/>. One
    /// row per migrated record. The <c>Payload</c> carries the legacy
    /// status as <c>legacyStatus</c> and the mapped reason code as
    /// <c>mappedReasonCode</c> so the audit trail remains coherent after
    /// the migration.
    /// </summary>
    AppealStatusMigrated = 9
}
