using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// Azure Service Bus implementation of <see cref="IMessageBus"/>. Owns a
/// single shared <see cref="ServiceBusClient"/> and lazily creates one
/// <see cref="ServiceBusSender"/> per queue or topic.
///
/// Receiving uses <see cref="ServiceBusProcessor"/> with
/// <c>AutoCompleteMessages=false</c>, preserving the CHO pattern:
///   - deserialize failure              → dead-letter ("DeserializeFailed")
///   - null payload after deserialize   → dead-letter ("NullPayload")
///   - handler success                  → complete
///   - handler throws                   → abandon (let SB redeliver up to
///                                         MaxDeliveryCount, then SB DLQs)
///
/// <c>Azure.Messaging.ServiceBus</c> already emits its own activities under
/// the <c>Azure.Messaging.ServiceBus.*</c> source — ensure
/// <see cref="ObservabilityExtensions.AddChoObservability"/> subscribes to
/// it. On top of that this class injects <c>traceparent</c> into
/// <see cref="ServiceBusMessage.ApplicationProperties"/> and restores the
/// parent activity context on receive so cross-service traces link up.
/// </summary>
public sealed class ServiceBusMessageBus : IMessageBus, IAsyncDisposable
{
    internal const string TraceparentPropertyName = "traceparent";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
    private readonly ILogger<ServiceBusMessageBus> _logger;
    private int _disposed;

    public ServiceBusMessageBus(
        ServiceBusClient client,
        ILogger<ServiceBusMessageBus> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SendAsync<T>(
        string queueOrTopic,
        T message,
        SendOptions? options = null,
        CancellationToken ct = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var sbMessage = BuildMessage(queueOrTopic, message, options);
        var sender = GetSender(queueOrTopic);

        using var producerSpan = ChoActivitySource.Instance.StartActivity(
            $"{queueOrTopic} send",
            ActivityKind.Producer);
        producerSpan?.SetTag("messaging.system", "azure-servicebus");
        producerSpan?.SetTag("messaging.destination.name", queueOrTopic);
        producerSpan?.SetTag("messaging.message.id", sbMessage.MessageId);

        RefreshTraceparent(sbMessage);

        await sender.SendMessageAsync(sbMessage, ct).ConfigureAwait(false);
    }

    public async Task ScheduleAsync<T>(
        string queueOrTopic,
        T message,
        DateTimeOffset enqueueAt,
        SendOptions? options = null,
        CancellationToken ct = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        var sbMessage = BuildMessage(queueOrTopic, message, options);
        var sender = GetSender(queueOrTopic);

        using var producerSpan = ChoActivitySource.Instance.StartActivity(
            $"{queueOrTopic} schedule",
            ActivityKind.Producer);
        producerSpan?.SetTag("messaging.system", "azure-servicebus");
        producerSpan?.SetTag("messaging.destination.name", queueOrTopic);
        producerSpan?.SetTag("messaging.message.id", sbMessage.MessageId);

        RefreshTraceparent(sbMessage);

        await sender.ScheduleMessageAsync(sbMessage, enqueueAt, ct).ConfigureAwait(false);
    }

    public IMessageSubscription Subscribe<T>(
        string queueOrTopic,
        Func<T, MessageContext, CancellationToken, Task> handler,
        SubscriptionOptions? options = null)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        var processorOptions = new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = options?.MaxConcurrentCalls ?? 4,
            AutoCompleteMessages = false // CHO pattern: complete explicitly
        };

        var processor = options?.SubscriptionName is not null
            ? _client.CreateProcessor(queueOrTopic, options.SubscriptionName, processorOptions)
            : _client.CreateProcessor(queueOrTopic, processorOptions);

        return new ServiceBusSubscription<T>(
            queueOrTopic,
            processor,
            handler,
            options?.RequiredProperties,
            _logger);
    }

    private ServiceBusSender GetSender(string queueOrTopic)
        => _senders.GetOrAdd(queueOrTopic, name => _client.CreateSender(name));

    private static ServiceBusMessage BuildMessage<T>(
        string queueOrTopic, T message, SendOptions? options)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);
        var sbMessage = new ServiceBusMessage(body)
        {
            MessageId = options?.MessageId ?? Guid.NewGuid().ToString("N"),
            CorrelationId = options?.CorrelationId ?? Activity.Current?.Id
        };
        if (options?.SessionId is not null) sbMessage.SessionId = options.SessionId;
        if (options?.Properties is not null)
        {
            foreach (var (k, v) in options.Properties)
                sbMessage.ApplicationProperties[k] = v;
        }
        return sbMessage;
    }

    private static void RefreshTraceparent(ServiceBusMessage sbMessage)
    {
        var current = Activity.Current;
        if (current is null) return;
        sbMessage.ApplicationProperties[TraceparentPropertyName] =
            $"00-{current.TraceId}-{current.SpanId}-{(current.Recorded ? "01" : "00")}";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var sender in _senders.Values)
        {
            try { await sender.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing ServiceBusSender");
            }
        }
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    internal sealed class ServiceBusSubscription<T> : IMessageSubscription
    {
        private readonly string _queueOrTopic;
        private readonly ServiceBusProcessor _processor;
        private readonly Func<T, MessageContext, CancellationToken, Task> _handler;
        private readonly IReadOnlyDictionary<string, string>? _requiredProperties;
        private readonly ILogger _logger;
        private int _started;

        public ServiceBusSubscription(
            string queueOrTopic,
            ServiceBusProcessor processor,
            Func<T, MessageContext, CancellationToken, Task> handler,
            IReadOnlyDictionary<string, string>? requiredProperties,
            ILogger logger)
        {
            _queueOrTopic = queueOrTopic;
            _processor = processor;
            _handler = handler;
            _requiredProperties = requiredProperties;
            _logger = logger;

            _processor.ProcessMessageAsync += OnMessageAsync;
            _processor.ProcessErrorAsync += OnErrorAsync;
        }

        public Task StartAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return Task.CompletedTask;
            return _processor.StartProcessingAsync(ct);
        }

        public Task StopAsync(CancellationToken ct)
            => _processor.IsProcessing ? _processor.StopProcessingAsync(ct) : Task.CompletedTask;

        private async Task OnMessageAsync(ProcessMessageEventArgs args)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in args.Message.ApplicationProperties)
                properties[k] = v?.ToString() ?? string.Empty;

            if (!MatchesRequiredProperties(properties))
            {
                _logger.LogDebug(
                    "Completing message {MessageId} on {Queue}: subscription property filter did not match",
                    args.Message.MessageId, _queueOrTopic);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
                return;
            }

            T? message;
            try
            {
                message = JsonSerializer.Deserialize<T>(args.Message.Body.ToString(), JsonOpts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Dead-lettering message {MessageId} on {Queue}: deserialize failed",
                    args.Message.MessageId, _queueOrTopic);
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "DeserializeFailed",
                    cancellationToken: args.CancellationToken).ConfigureAwait(false);
                return;
            }

            if (message is null)
            {
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "NullPayload",
                    cancellationToken: args.CancellationToken).ConfigureAwait(false);
                return;
            }

            ActivityContext parentCtx = default;
            var hasParent = properties.TryGetValue(TraceparentPropertyName, out var tp) &&
                ActivityContext.TryParse(tp, null, out parentCtx);

            using var consumerSpan = hasParent
                ? ChoActivitySource.Instance.StartActivity(
                    $"{_queueOrTopic} receive", ActivityKind.Consumer, parentCtx)
                : ChoActivitySource.Instance.StartActivity(
                    $"{_queueOrTopic} receive", ActivityKind.Consumer);

            consumerSpan?.SetTag("messaging.system", "azure-servicebus");
            consumerSpan?.SetTag("messaging.destination.name", _queueOrTopic);
            consumerSpan?.SetTag("messaging.message.id", args.Message.MessageId);
            consumerSpan?.SetTag("messaging.servicebus.delivery_count", args.Message.DeliveryCount);

            var ctx = new MessageContext(
                args.Message.MessageId,
                args.Message.CorrelationId,
                args.Message.DeliveryCount,
                properties);

            try
            {
                await _handler(message, ctx, args.CancellationToken).ConfigureAwait(false);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                consumerSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogWarning(ex,
                    "Abandoning message {MessageId} on {Queue}: handler threw (delivery {Delivery})",
                    args.Message.MessageId, _queueOrTopic, args.Message.DeliveryCount);
                await args.AbandonMessageAsync(args.Message,
                    cancellationToken: args.CancellationToken).ConfigureAwait(false);
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

        // Previously a silent no-op. Surface these at Warning level so a
        // production incident isn't masked by a missing diagnostic.
        private Task OnErrorAsync(ProcessErrorEventArgs args)
        {
            _logger.LogWarning(args.Exception,
                "ServiceBusProcessor error on {Queue} (source={Source}, entity={Entity})",
                _queueOrTopic, args.ErrorSource, args.EntityPath);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
            await _processor.DisposeAsync().ConfigureAwait(false);
        }
    }
}
