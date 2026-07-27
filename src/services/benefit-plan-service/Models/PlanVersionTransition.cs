using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace BenefitPlanService.Models;

/// <summary>
/// Audit record of a single state transition in a plan's version chain.
/// Append-only; one row per <see cref="PlanVersionTransitionType"/> event.
/// Partitioned on <c>TenantId</c> to match the rest of the service.
/// </summary>
[BsonIgnoreExtraElements]
public class PlanVersionTransition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    /// <summary>
    /// Predecessor <c>VersionId</c>. Null for the very first <c>Publish</c>
    /// of a brand-new plan.
    /// </summary>
    [JsonPropertyName("fromVersionId")]
    public string? FromVersionId { get; set; }

    /// <summary>
    /// Successor <c>VersionId</c>. Null for terminal supersessions with
    /// no replacement (reserved; current callers always supply one).
    /// </summary>
    [JsonPropertyName("toVersionId")]
    public string? ToVersionId { get; set; }

    [Required]
    [JsonPropertyName("transitionType")]
    public PlanVersionTransitionType TransitionType { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime? EffectiveDate { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("actorId")]
    public string? ActorId { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanVersionTransitionType
{
    /// <summary>Genesis Publish — no predecessor.</summary>
    Publish = 1,
    /// <summary>Draft created from an existing Published version.</summary>
    Amend = 2,
    /// <summary>Predecessor moved to <c>Superseded</c> by a new Published version.</summary>
    Supersede = 3,
    /// <summary>Published version moved to <c>Superseded</c> with no successor -- the plan ends.</summary>
    Terminate = 4
}
