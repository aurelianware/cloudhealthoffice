using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace EligibilityService.Services;

/// <summary>
/// Azure Service Bus implementation of <see cref="IBatchQueue"/>. Messages
/// are JSON-encoded <see cref="BatchQueueMessage"/>s with CorrelationId =
/// jobId for observability. No session grouping is used — job idempotency
/// is handled by <see cref="BatchEligibilityService.ProcessJobAsync"/>.
///
/// The queue consumer is <see cref="ServiceBusBatchQueueProcessor"/>
/// (see <see cref="IBatchQueueProcessor"/>); this class only handles enqueue.
/// <see cref="ReadAllAsync"/> throws — Service Bus messages are delivered
/// via the processor's push model, not polled.
/// </summary>
public class ServiceBusBatchQueue : IBatchQueue
{
    private readonly IBatchQueueSender _sender;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ServiceBusBatchQueue(IBatchQueueSender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public async ValueTask EnqueueAsync(BatchQueueMessage message, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);
        await _sender.SendAsync(body, correlationId: message.JobId, ct);
    }

    public IAsyncEnumerable<BatchQueueMessage> ReadAllAsync(CancellationToken ct)
        => throw new NotSupportedException(
            "ServiceBusBatchQueue uses push delivery; resolve IBatchQueueProcessor from DI instead.");
}

/// <summary>
/// Thin abstraction over <see cref="ServiceBusSender"/> so
/// <see cref="ServiceBusBatchQueue"/> is unit-testable without the emulator.
/// </summary>
public interface IBatchQueueSender
{
    Task SendAsync(byte[] body, string correlationId, CancellationToken ct);
}

public class ServiceBusSenderAdapter : IBatchQueueSender, IAsyncDisposable
{
    private readonly ServiceBusSender _sender;

    public ServiceBusSenderAdapter(ServiceBusClient client, string queueName)
    {
        _sender = client.CreateSender(queueName);
    }

    public async Task SendAsync(byte[] body, string correlationId, CancellationToken ct)
    {
        var message = new ServiceBusMessage(body)
        {
            CorrelationId = correlationId
        };
        await _sender.SendMessageAsync(message, ct);
    }

    public async ValueTask DisposeAsync() => await _sender.DisposeAsync();
}
