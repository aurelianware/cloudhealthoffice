using MongoDB.Bson.Serialization.Attributes;

namespace AppealsService.Models;

/// <summary>
/// One Kafka event waiting in an appeal's transactional outbox
/// (<see cref="Appeal.Outbox"/>). Written in the SAME single-document
/// update as the appeal change it describes, so the state change and its
/// event commit together or not at all. <c>AppealOutboxDispatcher</c>
/// publishes pending entries in array order (per-appeal order) and marks
/// them sent.
/// </summary>
/// <remarks>
/// <see cref="PayloadJson"/> is the complete, already-serialized wire
/// payload built by the field-whitelisted <c>AppealEventPublisher</c>
/// builders. It carries no encrypted-at-rest value.
/// </remarks>
[BsonIgnoreExtraElements]
public class AppealOutboxMessage
{
    /// <summary>
    /// Idempotency key. Equals the payload's <c>eventId</c>, the audit row's
    /// <see cref="AppealEvent.EventId"/> and the Kafka <c>event-id</c> header,
    /// so consumers de-duplicate a redelivery on it.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Kafka <c>event-type</c> header (e.g. <c>AppealStatusChanged</c>).</summary>
    public string EventType { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string AppealId { get; set; } = string.Empty;

    /// <summary>Serialized wire payload (camelCase JSON).</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AppealOutboxStatus Status { get; set; } = AppealOutboxStatus.Pending;

    /// <summary>Failed publish attempts that count toward dead-lettering.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the dispatcher may retry; <c>null</c> = now.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Last failure, sanitized; never payload content.</summary>
    public string? LastError { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>When the entry left <see cref="AppealOutboxStatus.Pending"/> (sent, skipped or dead-lettered).</summary>
    public DateTime? CompletedAt { get; set; }
}

public enum AppealOutboxStatus
{
    /// <summary>Waiting to be published.</summary>
    Pending = 1,

    /// <summary>Acknowledged by Kafka.</summary>
    Sent = 2,

    /// <summary>
    /// Failed <c>AppealOutbox:MaxAttempts</c> times with a non-transient
    /// error. No longer retried; replay with the outbox replay endpoint.
    /// </summary>
    DeadLettered = 3,

    /// <summary>
    /// Not published because Kafka is disabled by configuration and
    /// <c>AppealOutbox:SkipWhenKafkaDisabled</c> is true.
    /// </summary>
    Skipped = 4
}
