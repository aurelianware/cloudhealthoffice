using System.Collections.Concurrent;
using CloudHealthOffice.Infrastructure.Messaging;

namespace IdCardService.Services;

public class QnxtMirrorMessage
{
    public string TenantId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
}

public interface IQnxtMirrorQueue
{
    Task EnqueueMirrorAsync(QnxtMirrorMessage message, CancellationToken ct = default);

    /// <summary>In-process inspection used by reconciliation tests.</summary>
    IReadOnlyCollection<QnxtMirrorMessage> PeekEnqueued();
}

/// <summary>
/// In-memory implementation used when QNXT mirror is disabled or running in
/// dev/test. Records messages so reconciliation can observe + tests can assert.
/// </summary>
public class InMemoryQnxtMirrorQueue : IQnxtMirrorQueue
{
    private readonly ConcurrentQueue<QnxtMirrorMessage> _messages = new();

    public Task EnqueueMirrorAsync(QnxtMirrorMessage message, CancellationToken ct = default)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<QnxtMirrorMessage> PeekEnqueued() => _messages.ToArray();
}

/// <summary>
/// Production implementation that forwards onto the shared
/// <see cref="IMessageBus"/>. The bus owns the <c>ServiceBusClient</c>
/// lifecycle, which also fixes a handle-leak on shutdown: the previous
/// direct-ownership class implemented <see cref="IAsyncDisposable"/> but
/// Program.cs never disposed it.
/// </summary>
public class ServiceBusQnxtMirrorQueue : IQnxtMirrorQueue
{
    private readonly IMessageBus _bus;
    private readonly string _queueName;

    public ServiceBusQnxtMirrorQueue(IMessageBus bus, string queueName)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _queueName = queueName ?? throw new ArgumentNullException(nameof(queueName));
    }

    public Task EnqueueMirrorAsync(QnxtMirrorMessage message, CancellationToken ct = default)
        => _bus.SendAsync(_queueName, message, options: null, ct);

    public IReadOnlyCollection<QnxtMirrorMessage> PeekEnqueued() => Array.Empty<QnxtMirrorMessage>();
}
