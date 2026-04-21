using CloudHealthOffice.Infrastructure.Messaging;

namespace EligibilityService.Services;

/// <summary>
/// <see cref="IBatchQueue"/> backed by <see cref="IMessageBus"/>. The domain
/// wrapper exists so the rest of the service keeps calling
/// <c>EnqueueAsync</c> on a typed interface; the actual transport is owned
/// by the shared bus. Consumption is push-based via
/// <see cref="MessageBusBatchQueueProcessor"/>, so <see cref="ReadAllAsync"/>
/// is not supported.
/// </summary>
public class ServiceBusBatchQueue : IBatchQueue
{
    private readonly IMessageBus _bus;
    private readonly string _queueName;

    public ServiceBusBatchQueue(IMessageBus bus, string queueName)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _queueName = queueName ?? throw new ArgumentNullException(nameof(queueName));
    }

    public async ValueTask EnqueueAsync(BatchQueueMessage message, CancellationToken ct = default)
    {
        await _bus.SendAsync(
            _queueName,
            message,
            new SendOptions(CorrelationId: message.JobId),
            ct);
    }

    public IAsyncEnumerable<BatchQueueMessage> ReadAllAsync(CancellationToken ct)
        => throw new NotSupportedException(
            "ServiceBusBatchQueue uses push delivery; resolve IBatchQueueProcessor from DI instead.");
}
