using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ProviderService.Models;

/// <summary>
/// Append-only event emitted by <c>ProviderIntegrityProjectionService</c>
/// after a successful integrity-projection write-back (capability 5.4.5).
///
/// <para>
/// Mirrors <see cref="ProviderVersionEvent"/>: client-supplied
/// <see cref="EventId"/> for idempotency, monotonic per-aggregate
/// <see cref="Version"/>, partition key <c>{TenantId}:{ProviderId}</c>.
/// One event type today (<see cref="ProviderVerificationEventType.ProviderVerificationRefreshed"/>);
/// the type field exists so future capabilities can extend without
/// schema migration.
/// </para>
///
/// <para>
/// No cross-service consumer ships in this PR. Capability 5.10 (Verification
/// Integrity Score Surface) is the planned subscriber.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class ProviderVerificationEvent
{
    [Required]
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Client-supplied idempotency key. Format
    /// <c>refreshed:{providerId}:{verifiedAtUtcIso}</c>. Unique within
    /// <c>(TenantId, ProviderId)</c>.
    /// </summary>
    [Required]
    [StringLength(256)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public ProviderVerificationEventType EventType { get; set; }

    /// <summary>1-based monotonic sequence per <c>(TenantId, ProviderId)</c>.</summary>
    [Required]
    public int Version { get; set; }

    public int SchemaVersion { get; set; } = 1;

    [Required]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public int? IntegrityScore { get; set; }

    [StringLength(50)]
    public string? IntegrityRating { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset? NextVerificationDue { get; set; }

    [StringLength(200)]
    public string? ActorId { get; set; }

    [StringLength(128)]
    public string? CorrelationId { get; set; }

    [BsonIgnore]
    public JsonObject? Payload { get; set; }

    [JsonIgnore]
    public string? PayloadJson
    {
        get => Payload?.ToJsonString();
        set => Payload = string.IsNullOrEmpty(value)
            ? null
            : JsonNode.Parse(value) as JsonObject;
    }

    public static string BuildPartitionKey(string tenantId, string providerId) => $"{tenantId}:{providerId}";

    public static string BuildRefreshedEventId(string providerId, DateTimeOffset verifiedAt)
        => $"refreshed:{providerId}:{verifiedAt.UtcDateTime:O}";
}

/// <summary>
/// Mirrors PR #705 enum-handling pattern: <c>Unknown=0</c>, explicit
/// integer values, string serialization.
/// </summary>
public enum ProviderVerificationEventType
{
    Unknown = 0,
    ProviderVerificationRefreshed = 1,
}
