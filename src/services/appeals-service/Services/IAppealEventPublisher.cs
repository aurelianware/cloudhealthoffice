using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Emits appeal lifecycle events to Kafka. The DB is source of truth — a
/// Kafka failure is logged but never propagated.
/// </summary>
public interface IAppealEventPublisher
{
    /// <summary>Genesis event. <paramref name="fromStatus"/> is always null.</summary>
    Task PublishCreatedAsync(
        Appeal appeal,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>Any transition except genesis and close.</summary>
    Task PublishStatusChangedAsync(
        Appeal appeal,
        AppealStatus fromStatus,
        AppealStatus toStatus,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Terminal →Closed transition. Includes structured decision data
    /// (decisionType, approvedAmount) when a decision is present. Decision
    /// free-text fields (rationale, reviewer notes) remain encrypted at
    /// rest and are NOT in the payload.
    /// </summary>
    Task PublishClosedAsync(
        Appeal appeal,
        AppealStatus fromStatus,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    Task PublishNoteAddedAsync(
        Appeal appeal,
        AppealNote note,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    Task PublishAttachmentAddedAsync(
        Appeal appeal,
        AppealAttachment attachment,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    Task PublishAttachmentAcknowledgedAsync(
        Appeal appeal,
        AppealAttachment attachment,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>Read-time one-shot when an appeal first passes TargetResponseDate.</summary>
    Task PublishOverdueObservedAsync(
        Appeal appeal,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    Task PublishAssignedAsync(
        Appeal appeal,
        string? previousReviewerId,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Emitted by the status-migration hosted service for records
    /// carrying pre-modernization terminal status values that were rewritten
    /// to <c>Status=Closed</c> + <see cref="Appeal.ClosureReasonCode"/>. One
    /// event per migrated record.
    /// </summary>
    Task PublishStatusMigratedAsync(
        Appeal appeal,
        string legacyStatus,
        AppealClosureReasonCode mappedReasonCode,
        string actor,
        string? correlationId,
        CancellationToken ct = default);
}
