using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// Repository tests that build audit events by hand: attach the matching
/// Kafka event, as the controller does. Repositories refuse a mutating call
/// whose audit event carries no outbox message.
/// </summary>
public static class TestOutbox
{
    public static AppealEvent Queued(this AppealEvent e, Appeal a) => e.EventType switch
    {
        AppealEventType.AppealCreated => AppealOutbox.Created(e, a, e.CorrelationId),
        AppealEventType.AppealStatusChanged => AppealOutbox.StatusChanged(
            e, a, e.FromStatus ?? a.Status, e.ToStatus ?? a.Status, e.CorrelationId),
        AppealEventType.AppealClosed => AppealOutbox.Closed(e, a, e.FromStatus ?? a.Status, e.CorrelationId),
        AppealEventType.AppealNoteAdded => AppealOutbox.NoteAdded(e, a,
            new AppealNote { NoteId = e.Payload?["noteId"]?.GetValue<string>() ?? Guid.NewGuid().ToString(), CreatedBy = e.ActorId },
            e.CorrelationId),
        AppealEventType.AppealAttachmentAdded => AppealOutbox.AttachmentAdded(e, a,
            new AppealAttachment { AttachmentId = e.Payload?["attachmentId"]?.GetValue<string>() ?? Guid.NewGuid().ToString() },
            e.CorrelationId),
        AppealEventType.AppealAttachmentAcknowledged => AppealOutbox.AttachmentAcknowledged(e, a,
            new AppealAttachment { AttachmentId = e.Payload?["attachmentId"]?.GetValue<string>() ?? string.Empty, AcknowledgmentReceived = true },
            e.CorrelationId),
        AppealEventType.AppealOverdueObserved => AppealOutbox.OverdueObserved(e, a, e.CorrelationId),
        AppealEventType.AppealAssigned => AppealOutbox.Assigned(e, a, null, e.CorrelationId),
        AppealEventType.AppealDeadlineExtended => AppealOutbox.DeadlineExtended(e, a, e.CorrelationId),
        AppealEventType.AppealStatusMigrated => AppealOutbox.StatusMigrated(
            e, a, "Approved", AppealClosureReasonCode.Approved, e.CorrelationId),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.EventType, null)
    };
}
