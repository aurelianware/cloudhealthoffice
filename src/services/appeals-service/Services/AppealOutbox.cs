using System.Text.Json;
using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Builds the Kafka event for an appeal change and attaches it to the
/// change's audit row (<see cref="AppealEvent.OutboxMessage"/>), so the
/// repository writes it into the appeal's outbox in the same write as the
/// change. Payloads come from the field-whitelisted
/// <see cref="AppealEventPublisher"/> builders; the payload's
/// <c>eventId</c> and <c>occurredAt</c> are the audit row's, so a client
/// retry with the same idempotency key yields the same Kafka event id.
/// </summary>
public static class AppealOutbox
{
    public static AppealEvent Created(AppealEvent audit, Appeal a, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealCreatedType,
            AppealEventPublisher.BuildCreatedPayload(a, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    public static AppealEvent StatusChanged(
        AppealEvent audit, Appeal a, AppealStatus from, AppealStatus to, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealStatusChangedType,
            AppealEventPublisher.BuildStatusChangedPayload(a, from, to, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    public static AppealEvent Closed(AppealEvent audit, Appeal a, AppealStatus from, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealClosedType,
            AppealEventPublisher.BuildClosedPayload(a, from, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    public static AppealEvent NoteAdded(AppealEvent audit, Appeal a, AppealNote note, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealNoteAddedType,
            AppealEventPublisher.BuildNoteAddedPayload(a, note, audit.ActorId, correlationId,
                audit.EventId, audit.OccurredAt));

    public static AppealEvent AttachmentAdded(
        AppealEvent audit, Appeal a, AppealAttachment attachment, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealAttachmentAddedType,
            AppealEventPublisher.BuildAttachmentAddedPayload(a, attachment, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    /// <param name="acknowledged">The attachment as it will be persisted (ack flag and sent date applied).</param>
    public static AppealEvent AttachmentAcknowledged(
        AppealEvent audit, Appeal a, AppealAttachment acknowledged, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealAttachmentAcknowledgedType,
            AppealEventPublisher.BuildAttachmentAcknowledgedPayload(a, acknowledged, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    public static AppealEvent OverdueObserved(AppealEvent audit, Appeal a, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealOverdueObservedType,
            AppealEventPublisher.BuildOverdueObservedPayload(a, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    public static AppealEvent Assigned(
        AppealEvent audit, Appeal a, string? previousReviewerId, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealAssignedType,
            AppealEventPublisher.BuildAssignedPayload(a, previousReviewerId, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    /// <summary>Payload comes from <see cref="Appeal.DeadlineExtension"/>; its EventId must equal the audit row's.</summary>
    public static AppealEvent DeadlineExtended(AppealEvent audit, Appeal a, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealDeadlineExtendedType,
            AppealEventPublisher.BuildDeadlineExtendedPayload(a, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    public static AppealEvent StatusMigrated(
        AppealEvent audit, Appeal a, string legacyStatus, AppealClosureReasonCode mapped, string? correlationId) =>
        Attach(audit, AppealEventPublisher.AppealStatusMigratedType,
            AppealEventPublisher.BuildStatusMigratedPayload(a, legacyStatus, mapped, audit.ActorId, correlationId)
                with { EventId = audit.EventId, OccurredAt = audit.OccurredAt });

    private static AppealEvent Attach<TPayload>(AppealEvent audit, string eventType, TPayload payload)
    {
        audit.OutboxMessage = new AppealOutboxMessage
        {
            EventId = audit.EventId,
            EventType = eventType,
            TenantId = audit.TenantId,
            AppealId = audit.AppealId,
            PayloadJson = JsonSerializer.Serialize(payload, AppealEventPublisher.JsonOptions),
            CreatedAt = DateTime.UtcNow,
            Status = AppealOutboxStatus.Pending
        };
        return audit;
    }

    /// <summary>The outbox entries carried by <paramref name="events"/>, in order.</summary>
    public static List<AppealOutboxMessage> MessagesOf(IEnumerable<AppealEvent> events) =>
        events.Select(e => e.OutboxMessage).Where(m => m is not null).Select(m => m!).ToList();
}
