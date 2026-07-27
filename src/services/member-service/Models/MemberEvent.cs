using System;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberService.Models;

/// <summary>
/// Append-only event record for the member-events stream.
///
/// Idempotency: clients supply <see cref="EventId"/> on write; the repository rejects or
/// no-ops duplicates keyed on <c>(TenantId, MemberId, EventId)</c>.
///
/// Ordering: <see cref="Version"/> is a monotonically increasing per-aggregate sequence.
/// Concurrent writers to the same <c>(TenantId, MemberId)</c> must retry on conflict
/// (Cosmos optimistic ETag / Mongo unique index on <c>(tenantId, memberId, version)</c>).
///
/// Partition: Cosmos container uses <c>/partitionKey</c> (format <c>{tenantId}:{memberId}</c>)
/// so per-member Change Feed consumers see in-order events.
///
/// Genesis rule: <see cref="MemberEventType.MemberCreated"/> payloads MUST contain the full
/// member snapshot so projections can be rebuilt from the stream without special-casing.
/// Subsequent events SHOULD contain diffs (changed fields only).
/// </summary>
[BsonIgnoreExtraElements]
public class MemberEvent
{
    /// <summary>
    /// Cosmos document id. Defaults to <see cref="EventId"/> so duplicate writes collide.
    /// </summary>
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Cosmos partition key path = <c>{TenantId}:{MemberId}</c>.
    /// Stored as a first-class property so Cosmos/Mongo both see it.
    /// </summary>
    [Required]
    [StringLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Client-supplied idempotency key. Unique within <c>(TenantId, MemberId)</c>.
    /// </summary>
    [Required]
    [StringLength(128)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public MemberEventType EventType { get; set; }

    /// <summary>
    /// Monotonically increasing per-aggregate sequence number. 1-based.
    /// </summary>
    [Required]
    public int Version { get; set; }

    /// <summary>
    /// Envelope schema version. Bump when <see cref="MemberEvent"/> shape changes.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    [Required]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? ActorId { get; set; }

    [StringLength(128)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Event-specific payload. For <see cref="MemberEventType.MemberCreated"/>: full
    /// member snapshot. For updates: diff (changed fields only).
    ///
    /// Cosmos serialization (System.Text.Json) emits this as a nested JSON object.
    /// Mongo serialization goes through <see cref="PayloadJson"/> as a string because
    /// the BSON driver has no built-in serializer for <see cref="JsonObject"/>.
    /// </summary>
    [BsonIgnore]
    public JsonObject? Payload { get; set; }

    /// <summary>
    /// Mongo-facing mirror of <see cref="Payload"/>. Not emitted by System.Text.Json
    /// (Cosmos path) — only used by the BSON driver.
    /// </summary>
    [JsonIgnore]
    public string? PayloadJson
    {
        get => Payload?.ToJsonString();
        set => Payload = string.IsNullOrEmpty(value)
            ? null
            : JsonNode.Parse(value) as JsonObject;
    }

    public static string BuildPartitionKey(string tenantId, string memberId) =>
        $"{tenantId}:{memberId}";

    /// <summary>
    /// Builds the globally unique document id required by MongoDB's <c>_id</c>
    /// index. Event ids are only unique within a tenant/member stream, so using
    /// <see cref="EventId"/> directly would make otherwise independent tenants
    /// collide. Length-prefixing keeps the hash input unambiguous even when an
    /// identifier contains separator characters.
    /// </summary>
    public static string BuildMongoDocumentId(string tenantId, string memberId, string eventId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(memberId);
        ArgumentNullException.ThrowIfNull(eventId);

        var scopedId =
            $"{tenantId.Length}:{tenantId}{memberId.Length}:{memberId}{eventId.Length}:{eventId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopedId)));
    }
}

public enum MemberEventType
{
    MemberCreated = 1,
    MemberUpdated = 2,
    MemberTerminated = 3,
    AddressChanged = 4,
    PcpChanged = 5,

    // Alerts (FHIR Flag) — view events provide the access audit trail required
    // for protected member context like LitigationHold, CustodyDispute, etc.
    MemberAlertCreated = 6,
    MemberAlertEnded = 7,
    MemberAlertViewed = 8,

    // Notes (FHIR Communication) — immutable, audited on read for the same
    // PHI-access reason as alerts.
    MemberNoteCreated = 9,
    MemberNoteViewed = 10
}
