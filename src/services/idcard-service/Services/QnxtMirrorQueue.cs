using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

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

/// <summary>Azure Service Bus implementation for production.</summary>
public class ServiceBusQnxtMirrorQueue : IQnxtMirrorQueue, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public ServiceBusQnxtMirrorQueue(string connectionString, string queueName)
    {
        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(queueName);
    }

    public async Task EnqueueMirrorAsync(QnxtMirrorMessage message, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(message);
        await _sender.SendMessageAsync(new ServiceBusMessage(json), ct);
    }

    public IReadOnlyCollection<QnxtMirrorMessage> PeekEnqueued() => Array.Empty<QnxtMirrorMessage>();

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
