namespace CloudHealthOffice.Infrastructure.Messaging;

/// <summary>
/// No-op <see cref="IMessageBus"/>. Accepts sends and schedules silently
/// and returns subscriptions that never dispatch. Used for tests that want
/// to exercise code paths without bringing up any messaging plumbing.
///
/// Null arguments still throw — a misconfigured call site should surface
/// regardless of which backend happens to be wired in.
/// </summary>
public sealed class NullMessageBus : IMessageBus
{
    public Task SendAsync<T>(
        string queueOrTopic, T message, SendOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queueOrTopic);
        ArgumentNullException.ThrowIfNull(message);
        return Task.CompletedTask;
    }

    public Task ScheduleAsync<T>(
        string queueOrTopic, T message, DateTimeOffset enqueueAt,
        SendOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queueOrTopic);
        ArgumentNullException.ThrowIfNull(message);
        return Task.CompletedTask;
    }

    public IMessageSubscription Subscribe<T>(
        string queueOrTopic,
        Func<T, MessageContext, CancellationToken, Task> handler,
        SubscriptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(queueOrTopic);
        ArgumentNullException.ThrowIfNull(handler);
        return new NullSubscription();
    }

    private sealed class NullSubscription : IMessageSubscription
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
