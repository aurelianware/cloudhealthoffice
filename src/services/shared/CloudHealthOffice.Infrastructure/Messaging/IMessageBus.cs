namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// Shared async-messaging abstraction for Cloud Health Office services.
///
/// Three canonical patterns map onto this interface: work queues (single-
/// consumer-group per queue), pub-sub topics (multi-subscription), and
/// scheduled delivery. The production backend is Azure Service Bus; dev
/// and test environments use an in-process channel implementation.
///
/// Kafka usage (high-throughput streaming, event replay via change feeds)
/// stays on its own dedicated client — IMessageBus is not a Kafka facade.
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Enqueue <paramref name="message"/> for immediate delivery on
    /// <paramref name="queueOrTopic"/>.
    /// </summary>
    Task SendAsync<T>(
        string queueOrTopic,
        T message,
        SendOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Enqueue <paramref name="message"/> for delivery no earlier than
    /// <paramref name="enqueueAt"/>. Resolution is backend-specific —
    /// Service Bus honours to the second; the in-memory bus uses a timer
    /// and is suitable for tests only.
    /// </summary>
    Task ScheduleAsync<T>(
        string queueOrTopic,
        T message,
        DateTimeOffset enqueueAt,
        SendOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Create a subscription on <paramref name="queueOrTopic"/>. The
    /// returned subscription is stopped — call
    /// <see cref="IMessageSubscription.StartAsync"/> to begin dispatch.
    /// Complete-on-success / abandon-on-failure is handled by the backend;
    /// handlers must not swallow exceptions they wish to cause redelivery.
    /// </summary>
    IMessageSubscription Subscribe<T>(
        string queueOrTopic,
        Func<T, MessageContext, CancellationToken, Task> handler,
        SubscriptionOptions? options = null);
}

/// <summary>Per-message send-side options.</summary>
/// <param name="MessageId">Service Bus native deduplication key.</param>
/// <param name="CorrelationId">Overrides the ambient activity if provided.</param>
/// <param name="SessionId">For session-aware (ordered) queues; null otherwise.</param>
/// <param name="Properties">Application properties carried alongside the body.</param>
public record SendOptions(
    string? MessageId = null,
    string? CorrelationId = null,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>Per-subscription options.</summary>
/// <param name="MaxConcurrentCalls">Processor concurrency on the backend.</param>
/// <param name="AutoComplete">
/// Always false in CHO — we complete/abandon explicitly. The parameter exists
/// for completeness but the implementation ignores true.
/// </param>
/// <param name="SubscriptionName">Topic subscription name (topics only).</param>
/// <param name="RequiredProperties">
/// Optional application-property filter for local parity with Service Bus
/// subscription rules. Messages that do not contain every required key/value
/// are ignored by this subscriber.
/// </param>
public record SubscriptionOptions(
    int MaxConcurrentCalls = 4,
    bool AutoComplete = false,
    string? SubscriptionName = null,
    IReadOnlyDictionary<string, string>? RequiredProperties = null);

/// <summary>Context surfaced to handlers alongside the deserialized message.</summary>
public record MessageContext(
    string MessageId,
    string? CorrelationId,
    int DeliveryCount,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>Lifecycle control for a subscription created via <see cref="IMessageBus.Subscribe{T}"/>.</summary>
public interface IMessageSubscription : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
