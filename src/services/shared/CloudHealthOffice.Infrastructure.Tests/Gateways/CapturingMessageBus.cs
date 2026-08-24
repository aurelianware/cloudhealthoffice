using CloudHealthOffice.Infrastructure.Messaging;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

internal sealed class CapturingMessageBus : IMessageBus
{
    public List<(string Topic, object Message, SendOptions? Options)> Sent { get; } = new();

    public Task SendAsync<T>(
        string queueOrTopic,
        T message,
        SendOptions? options = null,
        CancellationToken ct = default)
    {
        Sent.Add((queueOrTopic, message!, options));
        return Task.CompletedTask;
    }

    public Task ScheduleAsync<T>(
        string queueOrTopic,
        T message,
        DateTimeOffset enqueueAt,
        SendOptions? options = null,
        CancellationToken ct = default) =>
        SendAsync(queueOrTopic, message, options, ct);

    public IMessageSubscription Subscribe<T>(
        string queueOrTopic,
        Func<T, MessageContext, CancellationToken, Task> handler,
        SubscriptionOptions? options = null) =>
        throw new NotSupportedException();
}

internal sealed class FailThenCaptureMessageBus : IMessageBus
{
    private int _remainingFailures;
    public List<(string Topic, object Message, SendOptions? Options)> Sent { get; } = new();

    public FailThenCaptureMessageBus(int failFirstSends) => _remainingFailures = failFirstSends;

    public Task SendAsync<T>(
        string queueOrTopic,
        T message,
        SendOptions? options = null,
        CancellationToken ct = default)
    {
        if (_remainingFailures > 0)
        {
            _remainingFailures--;
            throw new InvalidOperationException("message bus unavailable");
        }

        Sent.Add((queueOrTopic, message!, options));
        return Task.CompletedTask;
    }

    public Task ScheduleAsync<T>(
        string queueOrTopic,
        T message,
        DateTimeOffset enqueueAt,
        SendOptions? options = null,
        CancellationToken ct = default) =>
        SendAsync(queueOrTopic, message, options, ct);

    public IMessageSubscription Subscribe<T>(
        string queueOrTopic,
        Func<T, MessageContext, CancellationToken, Task> handler,
        SubscriptionOptions? options = null) =>
        throw new NotSupportedException();
}
