using CloudHealthOffice.Infrastructure.Messaging;

namespace EligibilityService.Services;

/// <summary>
/// Drives queue consumption. Has two implementations:
///   <see cref="ChannelBatchQueueProcessor"/> — drains the in-process channel
///     used by <see cref="InMemoryBatchQueue"/> (dev / single-instance).
///   <see cref="MessageBusBatchQueueProcessor"/> — subscribes to the shared
///     <see cref="IMessageBus"/>, which preserves the complete-on-success /
///     abandon-on-failure / dead-letter-on-deserialize pattern that lived
///     here previously as a direct <c>ServiceBusProcessor</c> wrapper.
///
/// <see cref="BatchEligibilityQueueWorker"/> resolves this from DI and
/// delegates; it does not know which backend it's on.
/// </summary>
public interface IBatchQueueProcessor
{
    Task RunAsync(
        Func<BatchQueueMessage, CancellationToken, Task> handler,
        CancellationToken stopping);
}

/// <summary>
/// Channel-based processor that drains an <see cref="InMemoryBatchQueue"/>.
/// TODO(addendum-a-7-1): fold into IMessageBus once the tests that depend
/// on IBatchQueue.ReadAllAsync are rewritten to subscribe-and-drain.
/// </summary>
public class ChannelBatchQueueProcessor : IBatchQueueProcessor
{
    private readonly IBatchQueue _queue;

    public ChannelBatchQueueProcessor(IBatchQueue queue)
    {
        _queue = queue;
    }

    public async Task RunAsync(
        Func<BatchQueueMessage, CancellationToken, Task> handler,
        CancellationToken stopping)
    {
        await foreach (var msg in _queue.ReadAllAsync(stopping))
        {
            await handler(msg, stopping);
        }
    }
}

/// <summary>
/// Processor that subscribes to <see cref="IMessageBus"/>. Behaviour
/// (complete / abandon / dead-letter) lives in the bus implementation;
/// this class just plumbs the typed handler through.
/// </summary>
public class MessageBusBatchQueueProcessor : IBatchQueueProcessor
{
    private readonly IMessageBus _bus;
    private readonly string _queueName;

    public MessageBusBatchQueueProcessor(IMessageBus bus, string queueName)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _queueName = queueName ?? throw new ArgumentNullException(nameof(queueName));
    }

    public async Task RunAsync(
        Func<BatchQueueMessage, CancellationToken, Task> handler,
        CancellationToken stopping)
    {
        await using var subscription = _bus.Subscribe<BatchQueueMessage>(
            _queueName,
            (msg, _, ct) => handler(msg, ct),
            new SubscriptionOptions(MaxConcurrentCalls: 4, AutoComplete: false));

        await subscription.StartAsync(stopping);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        await subscription.StopAsync(CancellationToken.None);
    }
}
