using CloudHealthOffice.Infrastructure.Messaging;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

public class ServiceBusBatchQueueTests
{
    [Fact]
    public async Task Enqueue_SendsMessageToBusWithJobIdCorrelation()
    {
        await using var bus = new InMemoryMessageBus();
        var queue = new ServiceBusBatchQueue(bus, "batch-eligibility");

        BatchQueueMessage? received = null;
        MessageContext? receivedCtx = null;
        var gate = new TaskCompletionSource();

        await using var subscription = bus.Subscribe<BatchQueueMessage>(
            "batch-eligibility",
            (msg, ctx, _) =>
            {
                received = msg;
                receivedCtx = ctx;
                gate.TrySetResult();
                return Task.CompletedTask;
            });
        await subscription.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(new BatchQueueMessage("tenant-X", "JOB-1"));

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(received);
        Assert.Equal("tenant-X", received!.TenantId);
        Assert.Equal("JOB-1", received.JobId);
        Assert.Equal("JOB-1", receivedCtx!.CorrelationId);
    }

    [Fact]
    public void ReadAllAsync_Unsupported()
    {
        var queue = new ServiceBusBatchQueue(new NullMessageBus(), "batch-eligibility");
        using var cts = new CancellationTokenSource();
        Assert.Throws<NotSupportedException>(() => queue.ReadAllAsync(cts.Token));
    }

    [Fact]
    public void NullBus_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceBusBatchQueue(null!, "q"));
    }

    [Fact]
    public void NullQueueName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceBusBatchQueue(new NullMessageBus(), null!));
    }
}
