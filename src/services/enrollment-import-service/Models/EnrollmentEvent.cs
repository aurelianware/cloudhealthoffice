using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace EnrollmentImportService.Models;

/// <summary>
/// Append-only event record for the enrollment-events stream.
///
/// Idempotency: the writer supplies <see cref="EventId"/>; the repository rejects duplicates
/// keyed on <c>(TenantId, MemberId, EventId)</c>. For 834 ingestion the EventId is derived
/// from <c>(BatchId, TransactionId, MemberId)</c> so replaying a batch yields zero new events.
///
/// Ordering: <see cref="Version"/> is monotonically increasing per (TenantId, MemberId).
/// Concurrent writers conflict on the unique-key policy on <c>/version</c>; the publisher
/// re-fetches the next version and retries.
///
/// Partition: Cosmos container <c>enrollment-events</c> uses partition key
/// <c>{tenantId}:{memberId}</c> so per-member change-feed consumers read in order.
/// </summary>
[BsonIgnoreExtraElements]
public class EnrollmentEvent
{
    [Required]
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("partitionKey")]
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("memberId")]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Client-supplied idempotency key, unique within (TenantId, MemberId).</summary>
    [Required]
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("eventType")]
    public EnrollmentEventType EventType { get; set; }

    [Required]
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Wall-clock time the event was recorded.</summary>
    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Business-effective date for the change (e.g. termination effective date).</summary>
    [JsonPropertyName("eventDate")]
    public DateTime? EventDate { get; set; }

    /// <summary>
    /// For retro adjustments only: the date the change is back-dated to. Always emitted
    /// as a separate field from <see cref="EventDate"/> so reconciliations can distinguish
    /// "when it was effective" (eventDate) from "when we acted" (occurredAt).
    /// </summary>
    [JsonPropertyName("retroEffectiveDate")]
    public DateTime? RetroEffectiveDate { get; set; }

    /// <summary>
    /// Originating batch id. For 834 ingestion this is the caller-supplied
    /// <see cref="Enrollment834.BatchId"/> or a generated <c>BATCH-&lt;ts&gt;-&lt;guid&gt;</c>.
    /// For manual submissions the controller synthesises <c>MANUAL-&lt;ts&gt;-&lt;guid&gt;</c>.
    /// It is NOT the source of idempotency on the manual path — see <see cref="EventId"/>
    /// and <see cref="BuildManualEventId"/>.
    /// </summary>
    [JsonPropertyName("sourceBatchId")]
    public string? SourceBatchId { get; set; }

    /// <summary>
    /// Per-transaction identifier. For 834 ingestion this is the 834 BGN02 when the
    /// caller supplies one, or the service-derived <c>{batchId}-{position}-{subscriberId}</c>
    /// otherwise. For manual submissions the service sets it to a synthesised id that
    /// lets transaction-log queries still group by batch; the per-event idempotency key
    /// on the manual path is the caller-supplied <c>EventId</c> (see
    /// <see cref="BuildManualEventId"/>), not this field.
    /// </summary>
    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }

    /// <summary>834 INS03 maintenance type code (021/001/024/030/etc.).</summary>
    [JsonPropertyName("maintenanceType")]
    public string? MaintenanceType { get; set; }

    /// <summary>834 INS04 maintenance reason code (e.g. EC, 41).</summary>
    [JsonPropertyName("maintenanceReason")]
    public string? MaintenanceReason { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "edi834";

    [JsonPropertyName("actorId")]
    public string? ActorId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Event-specific payload (changed fields, plan ids, addresses, etc.).
    /// Serialized via <see cref="PayloadJson"/> for MongoDB — the BSON driver
    /// has no built-in serializer for <see cref="JsonObject"/> (same approach
    /// as member-service's MemberEvent.Payload/PayloadJson).
    /// </summary>
    [JsonPropertyName("payload")]
    [BsonIgnore]
    public JsonObject? Payload { get; set; }

    /// <summary>Mongo-facing mirror of <see cref="Payload"/>. Not emitted by System.Text.Json.</summary>
    [JsonIgnore]
    public string? PayloadJson
    {
        get => Payload?.ToJsonString();
        set => Payload = string.IsNullOrEmpty(value)
            ? null
            : System.Text.Json.Nodes.JsonNode.Parse(value) as JsonObject;
    }

    /// <summary>
    /// Raw 834 snippet (or JSON form of the manual request) for audit / display.
    /// Truncated to keep documents under Cosmos's 2MB limit.
    /// </summary>
    [JsonPropertyName("rawSegment")]
    public string? RawSegment { get; set; }

    public static string BuildPartitionKey(string tenantId, string memberId) =>
        $"{tenantId}:{memberId}";

    /// <summary>
    /// EventId for an 834-ingested event. Prefix is fixed (<c>"834-"</c>) so it can never
    /// collide with manual EventIds even if an operator picks the same suffix.
    /// </summary>
    public static string BuildIngestEventId(string batchId, string transactionId, string memberId) =>
        $"834-{batchId}:{transactionId}:{memberId}";

    /// <summary>
    /// EventId for a manually-entered event. <paramref name="requestEventId"/> is the
    /// caller-supplied idempotency key (a fresh GUID if the caller didn't provide one).
    /// The prefix (<c>"manual-"</c>) prevents cross-path collisions with 834-derived ids.
    /// </summary>
    public static string BuildManualEventId(string requestEventId, string memberId) =>
        $"manual-{requestEventId}:{memberId}";
}

/// <summary>
/// Event types covered by the enrollment stream. Keep numeric values stable — they are
/// persisted in Cosmos.
/// </summary>
public enum EnrollmentEventType
{
    Enrolled = 1,
    Terminated = 2,
    PlanChanged = 3,
    AddressChanged = 4,
    PcpChanged = 5,
    SepTriggered = 6,
    CobraElected = 7,
    CobraTerminated = 8,
    RetroAdjusted = 9,
    ReinstatementApproved = 10
}
