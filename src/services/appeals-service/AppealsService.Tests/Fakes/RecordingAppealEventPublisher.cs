using System.Collections.Concurrent;
using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// Captures every publish call so controller + integration tests can
/// assert "one event published, exactly these fields, in this order".
/// One queue per event type keeps assertions simple.
/// </summary>
public sealed class RecordingAppealEventPublisher : IAppealEventPublisher
{
    public readonly ConcurrentQueue<CreatedCall> Created = new();
    public readonly ConcurrentQueue<StatusChangedCall> StatusChanged = new();
    public readonly ConcurrentQueue<ClosedCall> Closed = new();
    public readonly ConcurrentQueue<NoteAddedCall> NotesAdded = new();
    public readonly ConcurrentQueue<AttachmentAddedCall> AttachmentsAdded = new();
    public readonly ConcurrentQueue<AttachmentAckCall> AttachmentsAcknowledged = new();
    public readonly ConcurrentQueue<OverdueCall> OverdueObserved = new();
    public readonly ConcurrentQueue<AssignedCall> Assigned = new();
    public readonly ConcurrentQueue<MigratedCall> Migrated = new();

    /// <summary>
    /// Drain every queue. Used by the
    /// <see cref="Integration.AppealsWebApplicationFactory"/>'s test-scoped
    /// Reset hook — replacing the whole publisher between tests would
    /// desync the DI container (which caches the singleton at first build).
    /// </summary>
    public void Clear()
    {
        while (Created.TryDequeue(out _)) { }
        while (StatusChanged.TryDequeue(out _)) { }
        while (Closed.TryDequeue(out _)) { }
        while (NotesAdded.TryDequeue(out _)) { }
        while (AttachmentsAdded.TryDequeue(out _)) { }
        while (AttachmentsAcknowledged.TryDequeue(out _)) { }
        while (OverdueObserved.TryDequeue(out _)) { }
        while (Assigned.TryDequeue(out _)) { }
        while (Migrated.TryDequeue(out _)) { }
    }

    public Task PublishCreatedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default)
    {
        Created.Enqueue(new CreatedCall(appeal.Id, appeal.TenantId, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishStatusChangedAsync(
        Appeal appeal, AppealStatus fromStatus, AppealStatus toStatus,
        string actor, string? correlationId, CancellationToken ct = default)
    {
        StatusChanged.Enqueue(new StatusChangedCall(appeal.Id, appeal.TenantId, fromStatus, toStatus, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishClosedAsync(
        Appeal appeal, AppealStatus fromStatus, string actor, string? correlationId, CancellationToken ct = default)
    {
        Closed.Enqueue(new ClosedCall(appeal.Id, appeal.TenantId, fromStatus, appeal.ClosureReasonCode,
            appeal.Decision?.DecisionType, appeal.Decision?.ApprovedAmount, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishNoteAddedAsync(
        Appeal appeal, AppealNote note, string actor, string? correlationId, CancellationToken ct = default)
    {
        NotesAdded.Enqueue(new NoteAddedCall(appeal.Id, appeal.TenantId, note.NoteId, note.IsInternal, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishAttachmentAddedAsync(
        Appeal appeal, AppealAttachment attachment, string actor, string? correlationId, CancellationToken ct = default)
    {
        AttachmentsAdded.Enqueue(new AttachmentAddedCall(
            appeal.Id, appeal.TenantId, attachment.AttachmentId,
            attachment.AttachmentTypeCode, attachment.TransmissionCode,
            attachment.ControlNumber, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishAttachmentAcknowledgedAsync(
        Appeal appeal, AppealAttachment attachment, string actor, string? correlationId, CancellationToken ct = default)
    {
        AttachmentsAcknowledged.Enqueue(new AttachmentAckCall(
            appeal.Id, appeal.TenantId, attachment.AttachmentId,
            attachment.AcknowledgmentReceived, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishOverdueObservedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default)
    {
        OverdueObserved.Enqueue(new OverdueCall(appeal.Id, appeal.TenantId, appeal.Status, appeal.TargetResponseDate, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishAssignedAsync(
        Appeal appeal, string? previousReviewerId, string actor, string? correlationId, CancellationToken ct = default)
    {
        Assigned.Enqueue(new AssignedCall(appeal.Id, appeal.TenantId, appeal.AssignedReviewerId, previousReviewerId, actor, correlationId));
        return Task.CompletedTask;
    }

    public Task PublishStatusMigratedAsync(
        Appeal appeal, string legacyStatus, AppealClosureReasonCode mappedReasonCode,
        string actor, string? correlationId, CancellationToken ct = default)
    {
        Migrated.Enqueue(new MigratedCall(appeal.Id, appeal.TenantId, legacyStatus, mappedReasonCode, actor, correlationId));
        return Task.CompletedTask;
    }

    public sealed record CreatedCall(string AppealId, string TenantId, string Actor, string? CorrelationId);
    public sealed record StatusChangedCall(string AppealId, string TenantId, AppealStatus From, AppealStatus To, string Actor, string? CorrelationId);
    public sealed record ClosedCall(string AppealId, string TenantId, AppealStatus From, AppealClosureReasonCode? Reason, AppealDecisionType? DecisionType, decimal? ApprovedAmount, string Actor, string? CorrelationId);
    public sealed record NoteAddedCall(string AppealId, string TenantId, string NoteId, bool IsInternal, string Actor, string? CorrelationId);
    public sealed record AttachmentAddedCall(string AppealId, string TenantId, string AttachmentId, string AttachmentTypeCode, string TransmissionCode, string? ControlNumber, string Actor, string? CorrelationId);
    public sealed record AttachmentAckCall(string AppealId, string TenantId, string AttachmentId, bool AcknowledgmentReceived, string Actor, string? CorrelationId);
    public sealed record OverdueCall(string AppealId, string TenantId, AppealStatus Status, DateTime? TargetResponseDate, string Actor, string? CorrelationId);
    public sealed record AssignedCall(string AppealId, string TenantId, string? AssignedReviewerId, string? PreviousReviewerId, string Actor, string? CorrelationId);
    public sealed record MigratedCall(string AppealId, string TenantId, string LegacyStatus, AppealClosureReasonCode MappedReasonCode, string Actor, string? CorrelationId);
}
