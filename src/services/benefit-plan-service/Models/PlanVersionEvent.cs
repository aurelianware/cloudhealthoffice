using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace BenefitPlanService.Models;

/// <summary>
/// Append-only event for the plan-version stream. Mirrors
/// <c>MemberService.Models.MemberEvent</c>: client-supplied
/// <see cref="EventId"/> for idempotency, monotonic per-aggregate
/// <see cref="Version"/>, partition key <c>{TenantId}:{PlanId}</c>.
///
/// Only <see cref="PlanVersionEventType.PlanVersionPublished"/> and
/// <see cref="PlanVersionEventType.PlanVersionSuperseded"/> are emitted
/// today. Bus fan-out is intentionally not wired in this PR — the Mongo
/// stream is the system of record and downstream publishers can layer on
/// via decorator without touching call sites.
/// </summary>
[BsonIgnoreExtraElements]
public class PlanVersionEvent
{
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary><c>{TenantId}:{PlanId}</c> — first-class so Cosmos and Mongo both see it.</summary>
    [Required]
    [StringLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string PlanId { get; set; } = string.Empty;

    [Required]
    [StringLength(64)]
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Client-supplied idempotency key. Unique within <c>(TenantId, PlanId)</c>.</summary>
    [Required]
    [StringLength(128)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public PlanVersionEventType EventType { get; set; }

    /// <summary>1-based monotonic sequence per <c>(TenantId, PlanId)</c>.</summary>
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

    public static string BuildPartitionKey(string tenantId, string planId) => $"{tenantId}:{planId}";
}

public enum PlanVersionEventType
{
    PlanVersionPublished = 1,
    PlanVersionSuperseded = 2,
    PlanVersionTerminated = 3
}
