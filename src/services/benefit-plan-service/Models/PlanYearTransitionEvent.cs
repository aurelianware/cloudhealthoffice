using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace BenefitPlanService.Models;

/// <summary>
/// Append-only event for the plan-year transition stream
/// (5.3 — Plan-Year Definition Foundation). Mirrors
/// <see cref="PlanVersionEvent"/>: deterministic <see cref="EventId"/>
/// for idempotency, monotonic per-aggregate <see cref="Version"/>,
/// partition key <c>{TenantId}:{PlanId}</c>.
///
/// <para>
/// Two event types are emitted by <see cref="Services.PlanYearScheduler"/>:
/// <list type="bullet">
///   <item><see cref="PlanYearTransitionType.ApproachingTransition"/> — fires
///   once per plan-year-end when within the configured window (default 30
///   days). Lets accumulator-service warm caches and notify members.</item>
///   <item><see cref="PlanYearTransitionType.Transition"/> — fires once
///   when the plan-year-end has passed. Triggers reset / rollover work
///   in accumulator-service.</item>
/// </list>
/// Idempotency lives in the deterministic
/// <see cref="EventId"/> (<c>{type}:{tenantId}:{planId}:{yyyyMMdd}</c>),
/// so re-running the scheduler — or two replicas racing — produces a
/// single row in the stream.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class PlanYearTransitionEvent
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

    /// <summary>
    /// Deterministic idempotency key:
    /// <c>{transitionType}:{tenantId}:{planId}:{planYearEnd:yyyyMMdd}</c>.
    /// Re-running the scheduler will not insert a duplicate row.
    /// Sized at 256 to accommodate the full deterministic format using
    /// the model's allowed <see cref="TenantId"/> and <see cref="PlanId"/>
    /// lengths (each up to 100 chars).
    /// </summary>
    [Required]
    [StringLength(256)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public PlanYearTransitionType TransitionType { get; set; }

    [Required]
    public DateTime FromPlanYearEnd { get; set; }

    [Required]
    public DateTime ToPlanYearStart { get; set; }

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

    public static string BuildEventId(PlanYearTransitionType type, string tenantId, string planId, DateTime planYearEnd) =>
        $"{type.ToString().ToLowerInvariant()}:{tenantId}:{planId}:{planYearEnd:yyyyMMdd}";
}

public enum PlanYearTransitionType
{
    /// <summary>
    /// Plan-year-end is within the configured warning window (default 30
    /// days). One event per plan-year boundary; idempotent on reruns.
    /// </summary>
    ApproachingTransition = 1,

    /// <summary>
    /// Plan-year-end has passed. The accumulator-service is expected to
    /// reset / roll over targets per their <c>PlanYearResetBehavior</c>.
    /// </summary>
    Transition = 2
}
