using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ProviderService.Models;

/// <summary>
/// Append-only event for the provider-version stream. Mirrors
/// <c>BenefitPlanService.Models.PlanVersionEvent</c>: client-supplied
/// <see cref="EventId"/> for idempotency, monotonic per-aggregate
/// <see cref="Version"/>, partition key <c>{TenantId}:{ProviderId}</c>.
///
/// Five event types complete the state-machine observability surface:
/// <see cref="ProviderVersionEventType.ProviderVersionActivated"/>,
/// <see cref="ProviderVersionEventType.ProviderVersionSuperseded"/>,
/// <see cref="ProviderVersionEventType.ProviderVersionSuspended"/>,
/// <see cref="ProviderVersionEventType.ProviderVersionReactivated"/>,
/// <see cref="ProviderVersionEventType.ProviderVersionTerminated"/>.
/// </summary>
[BsonIgnoreExtraElements]
public class ProviderVersionEvent
{
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary><c>{TenantId}:{ProviderId}</c> — first-class so Cosmos and Mongo both see it.</summary>
    [Required]
    [StringLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string ProviderId { get; set; } = string.Empty;

    [Required]
    [StringLength(64)]
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Client-supplied idempotency key. Unique within <c>(TenantId, ProviderId)</c>.</summary>
    [Required]
    [StringLength(128)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public ProviderVersionEventType EventType { get; set; }

    /// <summary>1-based monotonic sequence per <c>(TenantId, ProviderId)</c>.</summary>
    [Required]
    public int Version { get; set; }

    public int SchemaVersion { get; set; } = 1;

    [Required]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

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
}

public enum ProviderVersionEventType
{
    ProviderVersionActivated = 1,
    ProviderVersionSuperseded = 2,
    ProviderVersionSuspended = 3,
    ProviderVersionReactivated = 4,
    ProviderVersionTerminated = 5
}
