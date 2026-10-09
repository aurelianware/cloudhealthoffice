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
/// <see cref="PayloadJson"/> is the wire payload built by the
/// field-whitelisted <c>AppealEventPublisher</c> builders; it carries no
/// encrypted-at-rest value. The dispatcher adds <see cref="Sequence"/> to it
/// at publish time.
/// </remarks>
[BsonIgnoreExtraElements]
public class AppealOutboxMessage
{
    /// <summary>
    /// Server-generated identity of this outbox row. Every dispatcher update
    /// matches on it (plus <c>Status == Pending</c> and the lease owner), so
    /// two rows can never be confused even if they share an
    /// <see cref="IdempotencyKey"/>.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The audit row's <see cref="AppealEvent.EventId"/> — the client's
    /// idempotency key when it supplied one. A write whose key is already in
    /// the outbox is a replay and appends nothing.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// Wire event id (payload <c>eventId</c> and <c>event-id</c> header):
    /// a UUIDv5 of <c>tenant:appeal:idempotencyKey</c>, so a client key
    /// reused across appeals or tenants never collides on the topic.
    /// Consumers de-duplicate on it.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Kafka <c>event-type</c> header (e.g. <c>AppealStatusChanged</c>).</summary>
    public string EventType { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string AppealId { get; set; } = string.Empty;

    /// <summary>Serialized wire payload (camelCase JSON) without <c>sequence</c>.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Per-appeal publish sequence (1, 2, ...) in write order, assigned by
    /// the lease holder just before the first publish attempt and kept for
    /// every redelivery and replay. Sent as payload <c>sequence</c> and the
    /// <c>event-sequence</c> header; consumers apply last-write-wins by it.
    /// </summary>
    public long? Sequence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AppealOutboxStatus Status { get; set; } = AppealOutboxStatus.Pending;

    /// <summary>Failed publish attempts that count toward dead-lettering.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the dispatcher may retry; <c>null</c> = now.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>Last failure (or dead-letter reason), sanitized; never payload content.</summary>
    public string? LastError { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    /// <summary>When the entry left <see cref="AppealOutboxStatus.Pending"/> (sent, skipped or dead-lettered).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Dead-lettered entries are pruned after this time (<c>AppealOutbox:DeadLetterRetention</c>).</summary>
    public DateTime? ExpiresAt { get; set; }
}

public enum AppealOutboxStatus
{
    /// <summary>Waiting to be published.</summary>
    Pending = 1,

    /// <summary>Acknowledged by Kafka.</summary>
    Sent = 2,

    /// <summary>
    /// Not delivered and no longer retried: rejected <c>AppealOutbox:MaxAttempts</c>
    /// times, or expired / over the per-appeal cap. Replay with the outbox
    /// replay endpoints; pruned after <c>AppealOutbox:DeadLetterRetention</c>.
    /// </summary>
    DeadLettered = 3,

    /// <summary>
    /// Not published because Kafka is disabled by configuration and
    /// <c>AppealOutbox:SkipWhenKafkaDisabled</c> is true.
    /// </summary>
    Skipped = 4
}
