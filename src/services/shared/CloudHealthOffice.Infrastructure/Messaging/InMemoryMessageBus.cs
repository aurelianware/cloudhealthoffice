using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// In-process <see cref="IMessageBus"/> backed by
/// <see cref="System.Threading.Channels"/>. Competing-consumer semantics
/// (one delivery per message across subscribers on the same queue) fall out
/// of channel-reader behaviour, so the same test asserting queue semantics
/// against Service Bus passes here too.
///
/// Scheduled delivery uses <see cref="Timer"/> — suitable for tests, not a
/// production path. Duplicate detection via <c>SendOptions.MessageId</c> is
/// emulated with an in-memory cache over <see cref="MessagingOptions.DuplicateDetectionWindow"/>
/// so the contract test ("idempotency via MessageId") passes identically on
/// both backends.
///
/// W3C trace context is carried via a <c>traceparent</c> application
/// property so a producer span started on enqueue becomes the parent of
/// the consumer span started on dispatch.
/// </summary>
public sealed class InMemoryMessageBus : IMessageBus, IAsyncDisposable
{
    internal const string TraceparentPropertyName = "traceparent";

    private readonly ConcurrentDictionary<string, Channel<Envelope>> _channels = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenMessageIds = new();
    private readonly ConcurrentBag<Timer> _scheduledTimers = new();
    private readonly ConcurrentBag<WeakReference<InMemorySubscription>> _subscriptions = new();
    private readonly TimeSpan _dedupWindow;
    private readonly ILogger<InMemoryMessageBus> _logger;

    public InMemoryMessageBus(
        MessagingOptions? options = null,
        ILogger<InMemoryMessageBus>? logger = null)
    {
        _dedupWindow = options?.DuplicateDetectionWindow ?? TimeSpan.FromHours(1);
        _logger = logger ?? NullLogger<InMemoryMessageBus>.Instance;
    }

    public Task SendAsync<T>(
        string queueOrTopic,
        T message,
        SendOptions? options = null,
        CancellationToken ct = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        ct.ThrowIfCancellationRequested();

        var envelope = BuildEnvelope(queueOrTopic, message!, options);
        if (envelope is null) return Task.CompletedTask; // deduplicated

        var channel = _channels.GetOrAdd(queueOrTopic, _ => Channel.CreateUnbounded<Envelope>());
        return channel.Writer.WriteAsync(envelope, ct).AsTask();
    }

    public Task ScheduleAsync<T>(
        string queueOrTopic,
        T message,
        DateTimeOffset enqueueAt,
        SendOptions? options = null,
        CancellationToken ct = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        ct.ThrowIfCancellationRequested();

        var delay = enqueueAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            return SendAsync(queueOrTopic, message, options, ct);
        }

        var envelope = BuildEnvelope(queueOrTopic, message!, options);
        if (envelope is null) return Task.CompletedTask;

        var channel = _channels.GetOrAdd(queueOrTopic, _ => Channel.CreateUnbounded<Envelope>());
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            channel.Writer.TryWrite(envelope);
            timer?.Dispose();
        }, null, delay, Timeout.InfiniteTimeSpan);
        _scheduledTimers.Add(timer);
        return Task.CompletedTask;
    }

    public IMessageSubscription Subscribe<T>(
        string queueOrTopic,
        Func<T, MessageContext, CancellationToken, Task> handler,
        SubscriptionOptions? options = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        var channel = _channels.GetOrAdd(queueOrTopic, _ => Channel.CreateUnbounded<Envelope>());
        var sub = new InMemorySubscription(
            queueOrTopic,
            channel,
            env => handler((T)env.Message, env.ToContext(), env.CancellationToken),
            Math.Max(1, options?.MaxConcurrentCalls ?? 1),
            options?.RequiredProperties,
            _logger);
        _subscriptions.Add(new WeakReference<InMemorySubscription>(sub));
        return sub;
    }

    private Envelope? BuildEnvelope(string queueOrTopic, object message, SendOptions? options)
    {
        var messageId = options?.MessageId ?? Guid.NewGuid().ToString("N");
        if (options?.MessageId is not null && !TryRecordMessageId(messageId))
        {
            _logger.LogDebug(
                "InMemoryMessageBus dropped duplicate message {MessageId} on {Queue}",
                messageId, queueOrTopic);
            return null;
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options?.Properties is not null)
        {
            foreach (var (k, v) in options.Properties) properties[k] = v;
        }

        // Capture the ambient activity before starting the producer span so
        // CorrelationId mirrors ServiceBusMessageBus: fall back to the
        // caller's activity id, not the producer span we're about to start.
        var correlationId = options?.CorrelationId ?? Activity.Current?.Id;

        using var producerSpan = ChoActivitySource.Instance.StartActivity(
            $"{queueOrTopic} send",
            ActivityKind.Producer);
        producerSpan?.SetTag("messaging.system", "in-memory");
        producerSpan?.SetTag("messaging.destination.name", queueOrTopic);
        producerSpan?.SetTag("messaging.message.id", messageId);

        var tp = Activity.Current;
        if (tp is not null)
        {
            properties[TraceparentPropertyName] =
                $"00-{tp.TraceId}-{tp.SpanId}-{(tp.Recorded ? "01" : "00")}";
        }

        return new Envelope(
            message,
            messageId,
            correlationId,
            properties);
    }

    private bool TryRecordMessageId(string messageId)
    {
        PruneMessageIdCache();
        return _seenMessageIds.TryAdd(messageId, DateTimeOffset.UtcNow);
    }

    private void PruneMessageIdCache()
    {
        var cutoff = DateTimeOffset.UtcNow - _dedupWindow;
        foreach (var entry in _seenMessageIds)
        {
            if (entry.Value < cutoff)
                _seenMessageIds.TryRemove(entry.Key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var timer in _scheduledTimers) timer.Dispose();
        foreach (var weak in _subscriptions)
        {
            if (weak.TryGetTarget(out var sub))
                await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed record Envelope(
        object Message,
        string MessageId,
        string? CorrelationId,
        IReadOnlyDictionary<string, string> Properties,
        int DeliveryCount = 1,
        CancellationToken CancellationToken = default)
    {
        public MessageContext ToContext()
            => new(MessageId, CorrelationId, DeliveryCount, Properties);
    }

    internal sealed class InMemorySubscription : IMessageSubscription
    {
        private readonly string _queueOrTopic;
        private readonly Channel<Envelope> _channel;
        private readonly Func<Envelope, Task> _dispatch;
        private readonly int _maxConcurrentCalls;
        private readonly IReadOnlyDictionary<string, string>? _requiredProperties;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _internalCts = new();
        private CancellationTokenSource? _linkedCts;
        private Task[]? _pumps;
        private int _started;
        private int _disposed;

        public InMemorySubscription(
            string queueOrTopic,
            Channel<Envelope> channel,
            Func<Envelope, Task> dispatch,
            int maxConcurrentCalls,
            IReadOnlyDictionary<string, string>? requiredProperties,
            ILogger logger)
        {
            _queueOrTopic = queueOrTopic;
            _channel = channel;
            _dispatch = dispatch;
            _maxConcurrentCalls = maxConcurrentCalls;
            _requiredProperties = requiredProperties;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) == 1) return Task.CompletedTask;
            if (Interlocked.Exchange(ref _started, 1) == 1) return Task.CompletedTask;
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_internalCts.Token, ct);
            var pumpToken = _linkedCts.Token;
            _pumps = Enumerable.Range(0, _maxConcurrentCalls)
                .Select(_ => Task.Run(() => PumpAsync(pumpToken), pumpToken))
                .ToArray();
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken ct)
        {
            if (_pumps is null) return;
            try { _internalCts.Cancel(); }
            catch (ObjectDisposedException) { return; }
            try
            {
                // Honour the caller's cancellation — don't let a hung handler
                // block StopAsync past the provided token's lifetime.
                await Task.WhenAll(_pumps).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected on stop or caller cancel */ }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            await foreach (var envelope in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await DispatchAsync(envelope, ct).ConfigureAwait(false);
            }
        }

        private async Task DispatchAsync(Envelope envelope, CancellationToken ct)
        {
            if (!MatchesRequiredProperties(envelope.Properties))
            {
                _logger.LogDebug(
                    "InMemoryMessageBus ignored {Queue} message {MessageId}: subscription property filter did not match",
                    _queueOrTopic, envelope.MessageId);
                return;
            }

            ActivityContext parentCtx = default;
            var hasParent = envelope.Properties.TryGetValue(TraceparentPropertyName, out var tp) &&
                ActivityContext.TryParse(tp, null, out parentCtx);

            using var consumerSpan = hasParent
                ? ChoActivitySource.Instance.StartActivity(
                    $"{_queueOrTopic} receive", ActivityKind.Consumer, parentCtx)
                : ChoActivitySource.Instance.StartActivity(
                    $"{_queueOrTopic} receive", ActivityKind.Consumer);

            consumerSpan?.SetTag("messaging.system", "in-memory");
            consumerSpan?.SetTag("messaging.destination.name", _queueOrTopic);
            consumerSpan?.SetTag("messaging.message.id", envelope.MessageId);

            var dispatched = envelope with { CancellationToken = ct };
            try
            {
                await _dispatch(dispatched).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                consumerSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex,
                    "InMemoryMessageBus handler threw for {Queue} message {MessageId}",
                    _queueOrTopic, envelope.MessageId);
                // In-memory has no DLQ; rethrow would kill the pump — swallow after logging.
            }
        }

        private bool MatchesRequiredProperties(IReadOnlyDictionary<string, string> properties)
        {
            if (_requiredProperties is null || _requiredProperties.Count == 0) return true;

            foreach (var (key, expectedValue) in _requiredProperties)
            {
                if (!properties.TryGetValue(key, out var actualValue) ||
                    !string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _internalCts.Dispose();
            _linkedCts?.Dispose();
        }
    }
}
