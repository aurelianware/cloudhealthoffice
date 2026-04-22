using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ConsentService.Models;

/// <summary>
/// Append-only audit row for <see cref="Consent"/>. One row per lifecycle
/// action (created, activated, revoked, expired). Partition key is
/// <c>{tenantId}:{consentId}</c> so the full audit trail for a consent lives
/// in a single partition and scans cheaply.
/// </summary>
[BsonIgnoreExtraElements]
public class ConsentEvent
{
    /// <summary>Cosmos document id + Mongo `_id`. Defaults to <see cref="EventId"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Cosmos partition key (see class remarks). Derived via <see cref="BuildPartitionKey"/>.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string ConsentId { get; set; } = string.Empty;

    [Required]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Client-supplied idempotency key. Duplicate appends with the same
    /// <c>EventId</c> are silently ignored at the repository layer.
    /// </summary>
    [Required]
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public ConsentEventType EventType { get; set; }

    public ConsentStatus? FromStatus { get; set; }
    public ConsentStatus? ToStatus { get; set; }

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
    /// <see cref="Consent"/> aggregate).
    ///
    /// Cosmos serialization (System.Text.Json) emits this as a nested JSON
    /// object. Mongo serialization goes through <see cref="PayloadJson"/>
    /// as a string because the BSON driver has no built-in serializer for
    /// <see cref="JsonObject"/>.
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

    public static string BuildPartitionKey(string tenantId, string consentId) => $"{tenantId}:{consentId}";
}

public enum ConsentEventType
{
    ConsentCreated = 1,
    ConsentActivated = 2,
    ConsentRevoked = 3,
    ConsentExpired = 4,
    ConsentViewed = 5
}
