using System.Threading.Channels;

namespace EligibilityService.Services;

/// <summary>
/// Abstraction over the queue that carries batch-eligibility jobs off the
/// request thread. Production maps this onto Azure Service Bus; tests and
/// single-instance deployments use the in-process channel below.
/// </summary>
public interface IBatchQueue
{
    ValueTask EnqueueAsync(BatchQueueMessage message, CancellationToken ct = default);
    IAsyncEnumerable<BatchQueueMessage> ReadAllAsync(CancellationToken ct);
}

public record BatchQueueMessage(string TenantId, string JobId);

/// <summary>
/// In-process queue based on System.Threading.Channels. Default binding for
/// IBatchQueue when no Service Bus connection string is configured.
/// </summary>
public class InMemoryBatchQueue : IBatchQueue
{
    private readonly Channel<BatchQueueMessage> _channel =
        Channel.CreateUnbounded<BatchQueueMessage>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(BatchQueueMessage message, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(message, ct);

    public IAsyncEnumerable<BatchQueueMessage> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
