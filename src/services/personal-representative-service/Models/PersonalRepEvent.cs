using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace PersonalRepresentativeService.Models;

/// <summary>
/// Append-only audit row for <see cref="PersonalRepresentative"/>. One row
/// per lifecycle action (created, activated, inactivated, expired,
/// association-added, association-removed). Partition key is
/// <c>{tenantId}:{personalRepId}</c> so the full audit trail for a rep
/// lives in a single partition and scans cheaply.
/// </summary>
[BsonIgnoreExtraElements]
public class PersonalRepEvent
{
    /// <summary>Cosmos document id + Mongo `_id`. Defaults to <see cref="EventId"/>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Cosmos partition key. Derived via <see cref="BuildPartitionKey"/>.</summary>
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string PersonalRepId { get; set; } = string.Empty;

    /// <summary>Populated on association events; null on rep-level events.</summary>
    [StringLength(50)]
    public string? MemberId { get; set; }

    /// <summary>
    /// Client-supplied idempotency key. Duplicate appends with the same
    /// <c>EventId</c> are silently ignored at the repository layer.
    /// </summary>
    [Required]
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public PersonalRepEventType EventType { get; set; }

    public PersonalRepStatus? FromStatus { get; set; }
    public PersonalRepStatus? ToStatus { get; set; }

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
    /// <see cref="PersonalRepresentative"/> aggregate).
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

    public static string BuildPartitionKey(string tenantId, string personalRepId) =>
        $"{tenantId}:{personalRepId}";
}

public enum PersonalRepEventType
{
    PersonalRepCreated = 1,
    PersonalRepActivated = 2,
    PersonalRepInactivated = 3,
    PersonalRepExpired = 4,
    PersonalRepAssociationAdded = 5,
    PersonalRepAssociationRemoved = 6
}
