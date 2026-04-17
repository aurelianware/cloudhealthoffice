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
///
/// Bounded with backpressure (FullMode = Wait) so a lagging or crashed worker
/// cannot cause the process to balloon its heap with pending batch messages.
/// Single-instance / dev use only — production should bind this interface to
/// a Service Bus implementation.
/// </summary>
public class InMemoryBatchQueue : IBatchQueue
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<BatchQueueMessage> _channel;

    public InMemoryBatchQueue() : this(DefaultCapacity) { }

    public InMemoryBatchQueue(int capacity)
    {
        _channel = Channel.CreateBounded<BatchQueueMessage>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    public ValueTask EnqueueAsync(BatchQueueMessage message, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(message, ct);

    public IAsyncEnumerable<BatchQueueMessage> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
