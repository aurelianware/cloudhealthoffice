using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Delivers one outbox entry to Kafka. Used only by
/// <c>AppealOutboxDispatcher</c>: nothing else publishes appeal events, so
/// an event can no longer be dropped by a publish that runs while Kafka is
/// down — it stays in the appeal's outbox until a produce is acknowledged.
/// </summary>
public interface IAppealEventTransport
{
    /// <summary>Completes when the transport has started, with the resulting state.</summary>
    Task<AppealEventPublisherState> Started { get; }

    /// <summary>
    /// Produce <paramref name="message"/> and wait for the broker's
    /// acknowledgement. Throws on any failure — never returns without
    /// delivering. Throws <see cref="AppealEventTransportUnavailableException"/>
    /// when the producer is not available.
    /// </summary>
    Task ProduceAsync(AppealOutboxMessage message, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="error"/> (thrown by <see cref="ProduceAsync"/>)
    /// is about the broker / network rather than this message — a Kafka
    /// outage. Transient failures back off but never dead-letter an event.
    /// </summary>
    bool IsTransient(Exception error);
}

/// <summary>Outcome of the Kafka transport's start.</summary>
public enum AppealEventPublisherState
{
    /// <summary>Producer built; events are produced.</summary>
    Available,

    /// <summary>
    /// <c>Kafka:BootstrapServers</c> is unset: publishing is off for the
    /// whole service by configuration. Events stay in the outbox (or are
    /// marked skipped when <c>AppealOutbox:SkipWhenKafkaDisabled</c> is set).
    /// </summary>
    Disabled,

    /// <summary>Kafka is configured but the producer could not be built. Events stay pending.</summary>
    Unavailable
}

/// <summary>The transport has no producer (not started, disabled, or failed to build).</summary>
public sealed class AppealEventTransportUnavailableException : Exception
{
    public AppealEventTransportUnavailableException(string message) : base(message) { }
}
