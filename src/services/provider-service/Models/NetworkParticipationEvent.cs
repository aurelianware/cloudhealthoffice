using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ProviderService.Models;

/// <summary>
/// Append-only event emitted by <c>NetworkParticipationBackfillService</c>
/// after a successful panel-gating patch on a single
/// <see cref="NetworkParticipation"/> row (capability 5.5).
///
/// <para>
/// Mirrors <see cref="ProviderVerificationEvent"/>: client-supplied
/// <see cref="EventId"/> for idempotency, monotonic per-aggregate
/// <see cref="Version"/>, partition key <c>{TenantId}:{ProviderId}</c>.
/// One event type today
/// (<see cref="NetworkParticipationEventType.PanelGatingBackfilled"/>);
/// the type field exists so future capabilities can extend without
/// schema migration.
/// </para>
///
/// <para>
/// No cross-service consumer ships in this PR. The audit trail is the
/// primary value — regulators and incident responders need a record of
/// when each participation's panel-gating was set, regardless of
/// whether a downstream consumer subscribes today.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class NetworkParticipationEvent
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
    /// <c>backfilled:{providerId}:{participationIndex}:{backfillRunId}</c>.
    /// Unique within <c>(TenantId, ProviderId)</c>. The
    /// <c>backfillRunId</c> ULID is generated per admin-endpoint
    /// invocation, so two separate operator runs of the backfill
    /// produce distinct events even for the same row.
    /// </summary>
    [Required]
    [StringLength(256)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public NetworkParticipationEventType EventType { get; set; }

    /// <summary>1-based monotonic sequence per <c>(TenantId, ProviderId)</c>.</summary>
    [Required]
    public int Version { get; set; }

    public int SchemaVersion { get; set; } = 1;

    [Required]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Zero-based index of the participation within
    /// <see cref="Provider.NetworkParticipations"/> at the time of
    /// the patch. Stable within a single Provider chain row.
    /// </summary>
    public int ParticipationIndex { get; set; }

    [StringLength(50)]
    public string? PlanId { get; set; }

    [StringLength(64)]
    public string? NetworkId { get; set; }

    public LineOfBusiness LineOfBusiness { get; set; }

    [StringLength(200)]
    public string? ActorId { get; set; }

    [StringLength(128)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// ULID for the admin-endpoint invocation that produced this
    /// event. Lets operators correlate every event written by a
    /// single backfill run, and ensures
    /// <see cref="EventId"/> uniqueness across reruns.
    /// </summary>
    [StringLength(64)]
    public string? BackfillRunId { get; set; }

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

    public static string BuildPartitionKey(string tenantId, string providerId) =>
        $"{tenantId}:{providerId}";

    public static string BuildBackfilledEventId(string providerId, int participationIndex, string backfillRunId)
        => $"backfilled:{providerId}:{participationIndex}:{backfillRunId}";
}

/// <summary>
/// Mirrors PR #705 enum-handling pattern: <c>Unknown=0</c>, explicit
/// integer values, string serialization.
/// </summary>
public enum NetworkParticipationEventType
{
    Unknown = 0,
    PanelGatingBackfilled = 1,
}
