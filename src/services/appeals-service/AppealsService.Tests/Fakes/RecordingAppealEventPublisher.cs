using System.Collections.Concurrent;
using System.Text.Json;
using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IAppealEventTransport"/>: records every produced
/// outbox entry, decoded from its wire payload into one queue per event
/// type, so controller + integration tests can assert "one event
/// published, exactly these fields, in this order". Failure injection
/// (<see cref="FailNext"/>, <see cref="Down"/>) drives the outbox retry,
/// backoff and dead-letter paths.
/// </summary>
public sealed class RecordingAppealEventPublisher : IAppealEventTransport
{
    private static readonly JsonSerializerOptions WireOptions = new(AppealEventPublisher.JsonOptions)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TaskCompletionSource<AppealEventPublisherState> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private readonly Queue<Exception> _failures = new();

    public RecordingAppealEventPublisher(AppealEventPublisherState state = AppealEventPublisherState.Available)
    {
        _started.TrySetResult(state);
    }

    public Task<AppealEventPublisherState> Started => _started.Task;

    /// <summary>Every delivered entry, in delivery order (duplicates included).</summary>
    public readonly ConcurrentQueue<AppealOutboxMessage> Produced = new();

    /// <summary>Produce attempts, delivered or not.</summary>
    public int Attempts;

    /// <summary>While true every produce throws a transient (broker unreachable) error.</summary>
    public volatile bool Down;

    public readonly ConcurrentQueue<CreatedCall> Created = new();
    public readonly ConcurrentQueue<StatusChangedCall> StatusChanged = new();
    public readonly ConcurrentQueue<ClosedCall> Closed = new();
    public readonly ConcurrentQueue<NoteAddedCall> NotesAdded = new();
    public readonly ConcurrentQueue<AttachmentAddedCall> AttachmentsAdded = new();
    public readonly ConcurrentQueue<AttachmentAckCall> AttachmentsAcknowledged = new();
    public readonly ConcurrentQueue<OverdueCall> OverdueObserved = new();
    public readonly ConcurrentQueue<AssignedCall> Assigned = new();
    public readonly ConcurrentQueue<MigratedCall> Migrated = new();
    public readonly ConcurrentQueue<DeadlineExtendedCall> DeadlineExtended = new();

    // Full wire payloads for tests that assert replay determinism.
    public readonly ConcurrentQueue<AppealNoteAddedEventPayload> NoteAddedPayloads = new();
    public readonly ConcurrentQueue<AppealDeadlineExtendedEventPayload> DeadlineExtendedPayloads = new();

    /// <summary>Make the next <paramref name="times"/> produces throw <paramref name="error"/>.</summary>
    public void FailNext(Exception error, int times = 1)
    {
        lock (_sync)
            for (var i = 0; i < times; i++) _failures.Enqueue(error);
    }

    /// <summary>A dispatcher that publishes inline through this transport (background loop off).</summary>
    public AppealOutboxDispatcher DispatcherFor(IAppealOutboxStore store, AppealOutboxOptions? options = null,
        TimeProvider? time = null) =>
        new(store, this, options ?? new AppealOutboxOptions
            {
                Enabled = false, InitialBackoff = TimeSpan.Zero, AwaitInlineDispatch = true
            },
            NullLogger<AppealOutboxDispatcher>.Instance, time);

    /// <summary>
    /// Drain every queue. Used by the
    /// <see cref="Integration.AppealsWebApplicationFactory"/>'s test-scoped
    /// Reset hook — replacing the whole publisher between tests would
    /// desync the DI container (which caches the singleton at first build).
    /// </summary>
    public void Clear()
    {
        lock (_sync) _failures.Clear();
        Down = false;
        Attempts = 0;
        while (Produced.TryDequeue(out _)) { }
        while (Created.TryDequeue(out _)) { }
        while (StatusChanged.TryDequeue(out _)) { }
        while (Closed.TryDequeue(out _)) { }
        while (NotesAdded.TryDequeue(out _)) { }
        while (AttachmentsAdded.TryDequeue(out _)) { }
        while (AttachmentsAcknowledged.TryDequeue(out _)) { }
        while (OverdueObserved.TryDequeue(out _)) { }
        while (Assigned.TryDequeue(out _)) { }
        while (Migrated.TryDequeue(out _)) { }
        while (DeadlineExtended.TryDequeue(out _)) { }
        while (NoteAddedPayloads.TryDequeue(out _)) { }
        while (DeadlineExtendedPayloads.TryDequeue(out _)) { }
    }

    public bool IsTransient(Exception error) => AppealEventPublisher.IsTransientError(error);

    public Task ProduceAsync(AppealOutboxMessage message, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        if (Down) throw new AppealEventTransportUnavailableException("broker unreachable (test)");
        lock (_sync)
        {
            if (_failures.TryDequeue(out var error)) throw error;
        }

        Produced.Enqueue(Copy(message));
        Record(message);
        return Task.CompletedTask;
    }

    /// <summary>The produced copy carries the wire payload (with <c>sequence</c>) as <c>PayloadJson</c>.</summary>
    private static AppealOutboxMessage Copy(AppealOutboxMessage m) => new()
    {
        Id = m.Id,
        IdempotencyKey = m.IdempotencyKey,
        Sequence = m.Sequence,
        EventId = m.EventId,
        EventType = m.EventType,
        TenantId = m.TenantId,
        AppealId = m.AppealId,
        PayloadJson = AppealOutbox.WirePayload(m),
        CreatedAt = m.CreatedAt,
        Status = m.Status,
        Attempts = m.Attempts
    };

    private static T Read<T>(AppealOutboxMessage m) => JsonSerializer.Deserialize<T>(m.PayloadJson, WireOptions)!;

    private static TEnum? EnumOrNull<TEnum>(string? value) where TEnum : struct, Enum =>
        string.IsNullOrEmpty(value) ? null : Enum.Parse<TEnum>(value);

    private void Record(AppealOutboxMessage m)
    {
        switch (m.EventType)
        {
            case AppealEventPublisher.AppealCreatedType:
            {
                var p = Read<AppealCreatedEventPayload>(m);
                Created.Enqueue(new CreatedCall(p.AppealId, p.TenantId, p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealStatusChangedType:
            {
                var p = Read<AppealStatusChangedEventPayload>(m);
                StatusChanged.Enqueue(new StatusChangedCall(p.AppealId, p.TenantId,
                    Enum.Parse<AppealStatus>(p.FromStatus), Enum.Parse<AppealStatus>(p.ToStatus), p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealClosedType:
            {
                var p = Read<AppealClosedEventPayload>(m);
                Closed.Enqueue(new ClosedCall(p.AppealId, p.TenantId, Enum.Parse<AppealStatus>(p.FromStatus),
                    EnumOrNull<AppealClosureReasonCode>(p.ClosureReasonCode),
                    EnumOrNull<AppealDecisionType>(p.DecisionType), p.ApprovedAmount, p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealNoteAddedType:
            {
                var p = Read<AppealNoteAddedEventPayload>(m);
                NotesAdded.Enqueue(new NoteAddedCall(p.AppealId, p.TenantId, p.NoteId, p.IsInternal, p.Actor, p.CorrelationId));
                NoteAddedPayloads.Enqueue(p);
                break;
            }
            case AppealEventPublisher.AppealAttachmentAddedType:
            {
                var p = Read<AppealAttachmentAddedEventPayload>(m);
                AttachmentsAdded.Enqueue(new AttachmentAddedCall(p.AppealId, p.TenantId, p.AttachmentId,
                    p.AttachmentTypeCode, p.TransmissionCode, p.ControlNumber, p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealAttachmentAcknowledgedType:
            {
                var p = Read<AppealAttachmentAcknowledgedEventPayload>(m);
                AttachmentsAcknowledged.Enqueue(new AttachmentAckCall(p.AppealId, p.TenantId, p.AttachmentId,
                    p.AcknowledgmentReceived, p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealOverdueObservedType:
            {
                var p = Read<AppealOverdueObservedEventPayload>(m);
                OverdueObserved.Enqueue(new OverdueCall(p.AppealId, p.TenantId, Enum.Parse<AppealStatus>(p.CurrentStatus),
                    p.TargetResponseDate, p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealAssignedType:
            {
                var p = Read<AppealAssignedEventPayload>(m);
                Assigned.Enqueue(new AssignedCall(p.AppealId, p.TenantId, p.AssignedReviewerId, p.PreviousReviewerId,
                    p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealStatusMigratedType:
            {
                var p = Read<AppealStatusMigratedEventPayload>(m);
                Migrated.Enqueue(new MigratedCall(p.AppealId, p.TenantId, p.LegacyStatus,
                    Enum.Parse<AppealClosureReasonCode>(p.MappedReasonCode), p.Actor, p.CorrelationId));
                break;
            }
            case AppealEventPublisher.AppealDeadlineExtendedType:
            {
                var p = Read<AppealDeadlineExtendedEventPayload>(m);
                DeadlineExtended.Enqueue(new DeadlineExtendedCall(p.AppealId, p.TenantId,
                    EnumOrNull<AppealExtensionReason>(p.Reason), p.ExtensionDays, p.TargetResponseDate, p.Actor, p.CorrelationId));
                DeadlineExtendedPayloads.Enqueue(p);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown event type {m.EventType}");
        }
    }

    public sealed record CreatedCall(string AppealId, string TenantId, string Actor, string? CorrelationId);
    public sealed record StatusChangedCall(string AppealId, string TenantId, AppealStatus From, AppealStatus To, string Actor, string? CorrelationId);
    public sealed record ClosedCall(string AppealId, string TenantId, AppealStatus From, AppealClosureReasonCode? Reason, AppealDecisionType? DecisionType, decimal? ApprovedAmount, string Actor, string? CorrelationId);
    public sealed record NoteAddedCall(string AppealId, string TenantId, string NoteId, bool IsInternal, string Actor, string? CorrelationId);
    public sealed record AttachmentAddedCall(string AppealId, string TenantId, string AttachmentId, string AttachmentTypeCode, string TransmissionCode, string? ControlNumber, string Actor, string? CorrelationId);
    public sealed record AttachmentAckCall(string AppealId, string TenantId, string AttachmentId, bool AcknowledgmentReceived, string Actor, string? CorrelationId);
    public sealed record OverdueCall(string AppealId, string TenantId, AppealStatus Status, DateTime? TargetResponseDate, string Actor, string? CorrelationId);
    public sealed record AssignedCall(string AppealId, string TenantId, string? AssignedReviewerId, string? PreviousReviewerId, string Actor, string? CorrelationId);
    public sealed record DeadlineExtendedCall(string AppealId, string TenantId, AppealExtensionReason? Reason, int? ExtensionDays, DateTime? TargetResponseDate, string Actor, string? CorrelationId);
    public sealed record MigratedCall(string AppealId, string TenantId, string LegacyStatus, AppealClosureReasonCode MappedReasonCode, string Actor, string? CorrelationId);
}
