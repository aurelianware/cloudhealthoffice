using AppealsService.Models;
using AppealsService.Services;
using Microsoft.Extensions.Hosting;

namespace AppealsService.Tests.Fakes;

/// <summary>
/// Behaves like <see cref="AppealEventPublisher"/> with respect to start-up:
/// a publish before <see cref="StartAsync"/> is skipped (counted in
/// <see cref="DroppedBeforeStart"/>), and <see cref="Started"/> completes
/// only when <see cref="StartAsync"/> runs — with <see cref="StartState"/>,
/// or never when <see cref="NeverStarts"/> is set. Delivered publishes are
/// recorded on <see cref="Inner"/>.
/// </summary>
public sealed class GatedAppealEventPublisher : IAppealEventPublisher, IAppealEventPublisherReadiness, IHostedService
{
    private readonly TaskCompletionSource<AppealEventPublisherState> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _available;
    private int _dropped;

    public RecordingAppealEventPublisher Inner { get; } = new();
    public AppealEventPublisherState StartState { get; init; } = AppealEventPublisherState.Available;
    public bool NeverStarts { get; init; }
    public int DroppedBeforeStart => _dropped;

    public Task<AppealEventPublisherState> Started => _started.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (NeverStarts) return Task.CompletedTask;
        _available = StartState == AppealEventPublisherState.Available;
        _started.TrySetResult(StartState);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task Gate(Func<Task> publish)
    {
        if (_available) return publish();
        Interlocked.Increment(ref _dropped);
        return Task.CompletedTask;
    }

    public Task PublishCreatedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishCreatedAsync(appeal, actor, correlationId, ct));

    public Task PublishStatusChangedAsync(Appeal appeal, AppealStatus fromStatus, AppealStatus toStatus, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishStatusChangedAsync(appeal, fromStatus, toStatus, actor, correlationId, ct));

    public Task PublishClosedAsync(Appeal appeal, AppealStatus fromStatus, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishClosedAsync(appeal, fromStatus, actor, correlationId, ct));

    public Task PublishNoteAddedAsync(Appeal appeal, AppealNote note, string actor, string? correlationId, CancellationToken ct = default, string? eventId = null, DateTime? occurredAt = null) =>
        Gate(() => Inner.PublishNoteAddedAsync(appeal, note, actor, correlationId, ct, eventId, occurredAt));

    public Task PublishAttachmentAddedAsync(Appeal appeal, AppealAttachment attachment, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishAttachmentAddedAsync(appeal, attachment, actor, correlationId, ct));

    public Task PublishAttachmentAcknowledgedAsync(Appeal appeal, AppealAttachment attachment, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishAttachmentAcknowledgedAsync(appeal, attachment, actor, correlationId, ct));

    public Task PublishOverdueObservedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishOverdueObservedAsync(appeal, actor, correlationId, ct));

    public Task PublishAssignedAsync(Appeal appeal, string? previousReviewerId, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishAssignedAsync(appeal, previousReviewerId, actor, correlationId, ct));

    public Task PublishDeadlineExtendedAsync(Appeal appeal, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishDeadlineExtendedAsync(appeal, actor, correlationId, ct));

    public Task PublishStatusMigratedAsync(Appeal appeal, string legacyStatus, AppealClosureReasonCode mappedReasonCode, string actor, string? correlationId, CancellationToken ct = default) =>
        Gate(() => Inner.PublishStatusMigratedAsync(appeal, legacyStatus, mappedReasonCode, actor, correlationId, ct));
}
