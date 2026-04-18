using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace AccumulatorService.Models;

/// <summary>
/// Append-only event-store row for accumulator mutations. Mirrors the pattern used
/// by member-service's MemberEvent stream so audit reconstruction is uniform across
/// services. Snapshot is a projection over these rows.
///
/// Unique constraints enforced at the persistence layer:
///   (tenantId, eventId)                        — wire-level de-dup
///   (tenantId, aggregateId, version)           — strict ordering per snapshot
/// </summary>
[BsonIgnoreExtraElements]
public class AccumulatorEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Partition key. Also the MongoDB _id for lookups.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Per-event id — matches the inbound event's EventId for ClaimApplied, fresh GUID for manual events.</summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Snapshot this event mutated. Matches AccumulatorSnapshot.Id.</summary>
    public string AggregateId { get; set; } = string.Empty;

    /// <summary>Snapshot version this event produced (post-write).</summary>
    public long Version { get; set; }

    public string MemberId { get; set; } = string.Empty;
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

    /// <summary>ClaimApplied | ManualAdjustment | OrphanSkipped | DuplicateSkipped.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>ClaimId for ClaimApplied/OrphanSkipped/DuplicateSkipped; AdjustmentId for ManualAdjustment.</summary>
    public string? SourceReference { get; set; }

    public string ActorId { get; set; } = "system";
    public string? Reason { get; set; }

    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }

    public List<ServiceAccumulatorDeltaRow> ServiceDeltas { get; set; } = new();

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Non-null only for event rows that originated from a ClaimFinalizedEvent.
    /// Used together with a unique index on (tenantId, sourceClaimId) to enforce
    /// idempotent application of the same claim across replayed messages.
    /// </summary>
    public string? SourceClaimId { get; set; }
}

public class ServiceAccumulatorDeltaRow
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal UsedDelta { get; set; }
    public string Unit { get; set; } = "USD";
}

/// <summary>
/// Persisted idempotency marker for ClaimFinalized consumption. The pair
/// (TenantId, ClaimId) is the idempotency key — re-finalization of the same
/// claim must be deduped even across event-id regenerations.
/// </summary>
[BsonIgnoreExtraElements]
public class ProcessedClaim
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>UTC timestamp the claim was applied; lets debugging trace "when did this get counted".</summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>AccumulatorEvent.Id that recorded the apply. Null for OrphanSkipped markers.</summary>
    public string? ResultingEventId { get; set; }

    /// <summary>Applied | OrphanSkipped.</summary>
    public string Outcome { get; set; } = "Applied";

    public static string BuildId(string tenantId, string claimId) => $"{tenantId}:{claimId}";
}
