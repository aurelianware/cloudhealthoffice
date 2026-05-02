using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// Append-only event for the claim-version stream. Mirrors
/// <c>ProviderService.Models.ProviderVersionEvent</c> and
/// <c>BenefitPlanService.Models.PlanVersionEvent</c>: client-supplied
/// <see cref="EventId"/> for idempotency, monotonic per-aggregate
/// <see cref="Version"/>, partition key <c>{TenantId}:{ClaimVersionId}</c>.
///
/// Seven event types cover the lifecycle transitions of a claim version:
/// <see cref="ClaimVersionEventType.ClaimVersionSubmitted"/>,
/// <see cref="ClaimVersionEventType.ClaimVersionAdjudicated"/>,
/// <see cref="ClaimVersionEventType.ClaimVersionPaid"/>,
/// <see cref="ClaimVersionEventType.ClaimVersionDenied"/>,
/// <see cref="ClaimVersionEventType.ClaimVersionSuperseded"/>,
/// <see cref="ClaimVersionEventType.ClaimVersionVoided"/>,
/// <see cref="ClaimVersionEventType.ClaimVersionReversed"/>.
///
/// Notes:
/// - <c>Pended</c> is not a version transition; pended claims remain in
///   <see cref="ClaimVersionState.Submitted"/> with structured
///   <see cref="PendDetails"/>. Pend/unpend is captured by the existing
///   Kafka <c>claims.pended.v1</c> topic, not the version stream.
/// - <c>Draft</c> creation is not an audit event; only state transitions
///   that affect downstream consumers emit version events.
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimVersionEvent
{
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary><c>{TenantId}:{ClaimVersionId}</c> — first-class so Cosmos and Mongo both see it.</summary>
    [Required]
    [StringLength(256)]
    public string PartitionKey { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Chain key — stable across all versions of a single claim. Mirrors
    /// <c>ProviderId</c> on the provider-version stream.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string ClaimVersionId { get; set; } = string.Empty;

    /// <summary>Per-version document id (the claim row that produced this event).</summary>
    [Required]
    [StringLength(64)]
    public string VersionId { get; set; } = string.Empty;

    /// <summary>Client-supplied idempotency key. Unique within <c>(TenantId, ClaimVersionId)</c>.</summary>
    [Required]
    [StringLength(128)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public ClaimVersionEventType EventType { get; set; }

    /// <summary>1-based monotonic sequence per <c>(TenantId, ClaimVersionId)</c>.</summary>
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

    public static string BuildPartitionKey(string tenantId, string claimVersionId) => $"{tenantId}:{claimVersionId}";
}

public enum ClaimVersionEventType
{
    ClaimVersionSubmitted = 1,
    ClaimVersionAdjudicated = 2,
    ClaimVersionPaid = 3,
    ClaimVersionDenied = 4,
    /// <summary>This version was superseded by an adjustment version. Mirrors <c>ProviderVersionSuperseded</c>.</summary>
    ClaimVersionSuperseded = 5,
    ClaimVersionVoided = 6,
    /// <summary>
    /// This version's accumulator impact (deductible/OOPM applied, payment to provider) was reversed
    /// as part of a 5.12 adjustment workflow. Distinct from <see cref="ClaimVersionSuperseded"/>:
    /// supersession marks the chain transition; <see cref="ClaimVersionReversed"/> signals downstream
    /// consumers (audit/lineage, future FHIR _history) that the prior accumulator state must be unwound.
    /// </summary>
    ClaimVersionReversed = 7
}
