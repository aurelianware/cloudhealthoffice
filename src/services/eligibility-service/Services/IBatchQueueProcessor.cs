using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace EligibilityService.Services;

/// <summary>
/// Drives queue consumption. Has two implementations:
///   ChannelBatchQueueProcessor — drains the in-process channel used by
///     <see cref="InMemoryBatchQueue"/> (dev / single-instance).
///   ServiceBusBatchQueueProcessor — wraps <see cref="ServiceBusProcessor"/>
///     with push delivery, complete-on-success / abandon-on-failure, and
///     lets Service Bus DLQ after MaxDeliveryCount.
///
/// <see cref="BatchEligibilityQueueWorker"/> resolves this from DI and
/// delegates; it does not know which backend it's on.
/// </summary>
public interface IBatchQueueProcessor
{
    /// <summary>
    /// Run until <paramref name="stopping"/> is cancelled, dispatching each
    /// received message to <paramref name="handler"/>.
    /// </summary>
    Task RunAsync(
        Func<BatchQueueMessage, CancellationToken, Task> handler,
        CancellationToken stopping);
}

/// <summary>
/// Channel-based processor that drains an <see cref="InMemoryBatchQueue"/>.
/// Used in dev and in unit tests.
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
/// Service Bus processor. Completes messages on handler success, abandons
/// on failure (redelivery up to MaxDeliveryCount, then queue DLQ).
/// </summary>
public class ServiceBusBatchQueueProcessor : IBatchQueueProcessor, IAsyncDisposable
{
    private readonly ServiceBusProcessor _processor;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ServiceBusBatchQueueProcessor(ServiceBusClient client, string queueName, int maxConcurrentCalls = 4)
    {
        _processor = client.CreateProcessor(queueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = maxConcurrentCalls,
            AutoCompleteMessages = false
        });
    }

    public async Task RunAsync(
        Func<BatchQueueMessage, CancellationToken, Task> handler,
        CancellationToken stopping)
    {
        _processor.ProcessMessageAsync += async args =>
        {
            BatchQueueMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<BatchQueueMessage>(
                    args.Message.Body.ToString(), JsonOpts);
            }
            catch
            {
                // Malformed payload — let SB auto-DLQ.
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "DeserializeFailed",
                    cancellationToken: args.CancellationToken);
                return;
            }

            if (msg == null)
            {
                await args.DeadLetterMessageAsync(args.Message,
                    deadLetterReason: "NullPayload",
                    cancellationToken: args.CancellationToken);
                return;
            }

            try
            {
                await handler(msg, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            }
            catch
            {
                await args.AbandonMessageAsync(args.Message,
                    cancellationToken: args.CancellationToken);
                throw;
            }
        };

        _processor.ProcessErrorAsync += _ => Task.CompletedTask;

        await _processor.StartProcessingAsync(stopping);
        try
        {
            await Task.Delay(Timeout.Infinite, stopping);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            await _processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync() => await _processor.DisposeAsync();
}
