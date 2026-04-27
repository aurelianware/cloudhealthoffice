using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ProviderService.Models;

/// <summary>
/// Append-only event in the credentialing workflow chain (capability 5.6).
/// Each provider has zero or more credentialing chains over its lifetime —
/// initial credentialing, then a re-credentialing chain every 2-3 years.
/// The chain is the system-of-record; the three flat fields on
/// <see cref="Provider"/> (<see cref="Provider.CredentialingStatus"/>,
/// <see cref="Provider.CredentialingDate"/>,
/// <see cref="Provider.RecredentialingDueDate"/>) are a denormalized
/// projection written via
/// <see cref="Repositories.IProviderRepository.UpdateCredentialingProjectionAsync"/>.
///
/// <para>
/// Mirrors <see cref="ProviderVerificationEvent"/>: client-supplied
/// <see cref="EventId"/> for idempotency, monotonic per-aggregate
/// <see cref="Version"/>, partition key <c>{TenantId}:{ProviderId}</c>,
/// Mongo <c>_id</c> scoped to <c>PartitionKey:EventId</c> for
/// cross-tenant collision protection.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class CredentialingEvent
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
    /// Client-supplied idempotency key. Format varies by event type — see
    /// the <c>Build*EventId</c> factories.
    /// </summary>
    [Required]
    [StringLength(256)]
    public string EventId { get; set; } = string.Empty;

    [Required]
    public CredentialingEventType EventType { get; set; }

    /// <summary>1-based monotonic sequence per <c>(TenantId, ProviderId)</c>.</summary>
    [Required]
    public int Version { get; set; }

    public int SchemaVersion { get; set; } = 1;

    [Required]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// EventId of the <see cref="CredentialingEventType.ApplicationSubmitted"/>
    /// that opened the current chain. Set on every event that belongs to
    /// an open application (PSV, CommitteeReviewScheduled, DecisionRecorded,
    /// ApplicationWithdrawn). Null on chain-management events
    /// (RecredentialingTriggered) and on the opening
    /// <see cref="CredentialingEventType.ApplicationSubmitted"/> itself
    /// (where the EventId IS the application identifier).
    /// </summary>
    [StringLength(256)]
    public string? ApplicationEventId { get; set; }

    /// <summary>
    /// EventId of the predecessor terminal
    /// <see cref="CredentialingEventType.DecisionRecorded"/>. Set on
    /// <see cref="CredentialingEventType.RecredentialingTriggered"/> to
    /// link the new chain to the prior approval.
    /// </summary>
    [StringLength(256)]
    public string? PredecessorEventId { get; set; }

    [StringLength(200)]
    public string? ActorId { get; set; }

    [StringLength(128)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Typed payload, serialized as a JSON object on the wire. Mirrors
    /// the verification-event bridge: <see cref="Payload"/> is in-memory,
    /// <see cref="PayloadJson"/> is the Mongo-persisted column. Each event
    /// type has its own payload record under <see cref="CredentialingPayloads"/>.
    /// </summary>
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

    public static string BuildPartitionKey(string tenantId, string providerId)
        => $"{tenantId}:{providerId}";

    // EventId factories — deterministic per event-type so re-publishing the
    // same logical event collapses to a no-op via the idempotency probe.
    public static string BuildApplicationSubmittedEventId(string providerId, DateTimeOffset submittedAt)
        => $"applicationSubmitted:{providerId}:{submittedAt.UtcDateTime:O}";

    /// <summary>
    /// EventId for the application synthesized by the legacy
    /// <c>PUT /providers/{id}/credentialing</c> shim when no open chain
    /// exists. Deterministic on the paired
    /// <see cref="CredentialingEventType.DecisionRecorded"/> EventId so
    /// retries of the legacy endpoint collapse to the same synthesized
    /// row.
    /// </summary>
    public static string BuildSynthesizedApplicationSubmittedEventId(string providerId, string decisionEventId)
        => $"synthesized-application:{providerId}:{decisionEventId}";

    public static string BuildPrimarySourceVerificationEventId(string providerId, string applicationEventId, DateTimeOffset verifiedAt)
        => $"psv:{providerId}:{applicationEventId}:{verifiedAt.UtcDateTime:O}";

    public static string BuildCommitteeReviewScheduledEventId(string providerId, string applicationEventId, DateTimeOffset scheduledFor)
        => $"committeeScheduled:{providerId}:{applicationEventId}:{scheduledFor.UtcDateTime:O}";

    public static string BuildDecisionRecordedEventId(string providerId, string applicationEventId, DateTimeOffset decidedAt)
        => $"decision:{providerId}:{applicationEventId}:{decidedAt.UtcDateTime:O}";

    public static string BuildRecredentialingTriggeredEventId(string providerId, DateTimeOffset triggeredAt)
        => $"recredential:{providerId}:{triggeredAt.UtcDateTime:O}";

    public static string BuildApplicationWithdrawnEventId(string providerId, string applicationEventId, DateTimeOffset withdrawnAt)
        => $"withdrawn:{providerId}:{applicationEventId}:{withdrawnAt.UtcDateTime:O}";
}

/// <summary>
/// Event types in the credentialing workflow chain. Mirrors PR #705
/// enum-handling pattern: <c>Unknown=0</c>, explicit integer values,
/// string serialization on the wire.
/// </summary>
public enum CredentialingEventType
{
    Unknown = 0,
    ApplicationSubmitted = 1,
    PrimarySourceVerificationCompleted = 2,
    CommitteeReviewScheduled = 3,
    DecisionRecorded = 4,
    RecredentialingTriggered = 5,
    ApplicationWithdrawn = 6,
}

/// <summary>
/// Outcome of a credentialing committee decision. Carried in
/// <see cref="CredentialingPayloads.DecisionRecordedPayload.Decision"/>.
/// </summary>
public enum CredentialingDecision
{
    Unknown = 0,
    Approved = 1,
    Denied = 2,
}

/// <summary>
/// Authority that recorded a credentialing decision. The
/// <see cref="DelegatedAuthority"/> path is used by the legacy
/// <c>PUT /providers/{id}/credentialing</c> shim — the service synthesizes
/// the missing application event when no chain is open so the legacy
/// endpoint stays always-succeeds-on-Active providers.
/// </summary>
public enum DecisionAuthorityType
{
    Unknown = 0,
    CredentialingCommittee = 1,
    MedicalDirector = 2,
    DelegatedAuthority = 3,
    AutoApproved = 4,
}
