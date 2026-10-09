using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Builds the Kafka event for an appeal change and attaches it to the
/// change's audit row (<see cref="AppealEvent.OutboxMessage"/>), so the
/// repository writes it into the appeal's outbox in the same write as the
/// change. Payloads come from the field-whitelisted
/// <see cref="AppealEventPublisher"/> builders. The payload's
/// <c>occurredAt</c> is the audit row's, and its <c>eventId</c> is
/// <see cref="WireEventId"/> of the audit row's EventId (the client's
/// idempotency key), so a client retry with the same key yields the same
/// Kafka event id while keys reused across appeals or tenants never collide.
/// </summary>
public static class AppealOutbox
{
    public static AppealEvent Created(AppealEvent audit, Appeal a, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealCreatedType,
            AppealEventPublisher.BuildCreatedPayload(a, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    public static AppealEvent StatusChanged(
        AppealEvent audit, Appeal a, AppealStatus from, AppealStatus to, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealStatusChangedType,
            AppealEventPublisher.BuildStatusChangedPayload(a, from, to, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    public static AppealEvent Closed(AppealEvent audit, Appeal a, AppealStatus from, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealClosedType,
            AppealEventPublisher.BuildClosedPayload(a, from, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    public static AppealEvent NoteAdded(AppealEvent audit, Appeal a, AppealNote note, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealNoteAddedType,
            AppealEventPublisher.BuildNoteAddedPayload(a, note, audit.ActorId, correlationId,
                Wire(audit), audit.OccurredAt));

    public static AppealEvent AttachmentAdded(
        AppealEvent audit, Appeal a, AppealAttachment attachment, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealAttachmentAddedType,
            AppealEventPublisher.BuildAttachmentAddedPayload(a, attachment, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    /// <param name="acknowledged">The attachment as it will be persisted (ack flag and sent date applied).</param>
    public static AppealEvent AttachmentAcknowledged(
        AppealEvent audit, Appeal a, AppealAttachment acknowledged, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealAttachmentAcknowledgedType,
            AppealEventPublisher.BuildAttachmentAcknowledgedPayload(a, acknowledged, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    public static AppealEvent OverdueObserved(AppealEvent audit, Appeal a, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealOverdueObservedType,
            AppealEventPublisher.BuildOverdueObservedPayload(a, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    public static AppealEvent Assigned(
        AppealEvent audit, Appeal a, string? previousReviewerId, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealAssignedType,
            AppealEventPublisher.BuildAssignedPayload(a, previousReviewerId, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    /// <summary>Payload comes from <see cref="Appeal.DeadlineExtension"/>; its EventId must equal the audit row's.</summary>
    public static AppealEvent DeadlineExtended(AppealEvent audit, Appeal a, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealDeadlineExtendedType,
            AppealEventPublisher.BuildDeadlineExtendedPayload(a, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    public static AppealEvent StatusMigrated(
        AppealEvent audit, Appeal a, string legacyStatus, AppealClosureReasonCode mapped, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealStatusMigratedType,
            AppealEventPublisher.BuildStatusMigratedPayload(a, legacyStatus, mapped, audit.ActorId, correlationId)
                with { EventId = Wire(audit), OccurredAt = audit.OccurredAt });

    private static AppealEvent Attach<TPayload>(AppealEvent audit, string eventType, TPayload payload)
    {
        audit.OutboxMessage = new AppealOutboxMessage
        {
            IdempotencyKey = audit.EventId,
            EventId = Wire(audit),
            EventType = eventType,
            TenantId = audit.TenantId,
            AppealId = audit.AppealId,
            PayloadJson = JsonSerializer.Serialize(payload, AppealEventPublisher.JsonOptions),
            CreatedAt = DateTime.UtcNow,
            Status = AppealOutboxStatus.Pending
        };
        return audit;
    }

    /// <summary>The outbox entries carried by <paramref name="events"/>, in order; each must carry one.</summary>
    public static List<AppealOutboxMessage> MessagesOf(IEnumerable<AppealEvent> events) =>
        events.Select(Require).ToList();

    private static string Wire(AppealEvent audit) => WireEventId(audit.TenantId, audit.AppealId, audit.EventId);

    /// <summary>RFC 4122 namespace for appeal wire event ids (fixed forever).</summary>
    private static readonly Guid WireNamespace = new("6f0c2a52-3b8e-5d4a-9b1e-0a7c3e2d1f40");

    /// <summary>
    /// Deterministic wire event id: UUIDv5 (SHA-1, RFC 4122 §4.3) of
    /// <c>{tenantId}:{appealId}:{idempotencyKey}</c>. The same change retried
    /// with the same key always maps to the same id; the same key on another
    /// appeal or tenant never does.
    /// </summary>
    public static string WireEventId(string tenantId, string appealId, string idempotencyKey)
    {
        var ns = WireNamespace.ToByteArray();
        SwapGuidBytes(ns); // to network order
        var name = Encoding.UTF8.GetBytes($"{tenantId}:{appealId}:{idempotencyKey}");
        var input = new byte[ns.Length + name.Length];
        Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
        Buffer.BlockCopy(name, 0, input, ns.Length, name.Length);
#pragma warning disable CA5350 // SHA-1 is mandated by UUIDv5; not a security use.
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        SwapGuidBytes(bytes); // back to Guid's little-endian layout
        return new Guid(bytes).ToString();
    }

    private static void SwapGuidBytes(byte[] g)
    {
        (g[0], g[3]) = (g[3], g[0]);
        (g[1], g[2]) = (g[2], g[1]);
        (g[4], g[5]) = (g[5], g[4]);
        (g[6], g[7]) = (g[7], g[6]);
    }

    /// <summary>The payload as published: <see cref="AppealOutboxMessage.PayloadJson"/> plus <c>sequence</c>.</summary>
    public static string WirePayload(AppealOutboxMessage message)
    {
        if (message.Sequence is not { } sequence) return message.PayloadJson;
        var node = JsonNode.Parse(message.PayloadJson) as JsonObject
                   ?? throw new InvalidOperationException("Outbox payload is not a JSON object.");
        node["sequence"] = sequence;
        return node.ToJsonString();
    }

    /// <summary>
    /// A mutating repository call must carry its Kafka event: an appeal
    /// change without one would silently never reach consumers.
    /// </summary>
    public static AppealOutboxMessage Require(AppealEvent auditEvent) =>
        auditEvent.OutboxMessage
        ?? throw new InvalidOperationException(
            $"Audit event {auditEvent.EventType} for appeal {auditEvent.AppealId} carries no outbox message; " +
            "attach one with AppealOutbox before writing the change.");
}
