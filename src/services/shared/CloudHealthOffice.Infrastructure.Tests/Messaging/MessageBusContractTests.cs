using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;

namespace CloudHealthOffice.Infrastructure.Tests.Messaging;

/// <summary>
/// Shared contract every <see cref="IMessageBus"/> implementation must pass.
///
/// The InMemory fixture runs in every CI build. A Service Bus fixture
/// deriving from this class can be wired up and executed against a real
/// namespace; keeping the assertions shared means "in-memory works, prod
/// is subtly different" can't silently escape notice.
/// </summary>
public abstract class MessageBusContractTests : IAsyncLifetime
{
    protected IMessageBus Bus { get; private set; } = default!;
    private IAsyncDisposable? _owned;

    protected abstract ValueTask<IMessageBus> CreateBusAsync();
    protected virtual string QueueNameFor(string test) => $"contract-{test}-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        Bus = await CreateBusAsync();
        _owned = Bus as IAsyncDisposable;
    }

    public async Task DisposeAsync()
    {
        if (_owned is not null) await _owned.DisposeAsync();
    }

    [SkippableFact]
    public async Task SendAsync_RoundTripsMessage()
    {
        var queue = QueueNameFor("roundtrip");
        var received = new TaskCompletionSource<Payload>();
        await using var sub = Bus.Subscribe<Payload>(queue, (msg, _, _) =>
        {
            received.TrySetResult(msg);
            return Task.CompletedTask;
        });
        await sub.StartAsync(CancellationToken.None);

        await Bus.SendAsync(queue, new Payload("hello", 42));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello", msg.Name);
        Assert.Equal(42, msg.Value);
    }

    [SkippableFact]
    public async Task SendAsync_PropagatesCorrelationIdAndProperties()
    {
        var queue = QueueNameFor("corr");
        var received = new TaskCompletionSource<MessageContext>();
        await using var sub = Bus.Subscribe<Payload>(queue, (_, ctx, _) =>
        {
            received.TrySetResult(ctx);
            return Task.CompletedTask;
        });
        await sub.StartAsync(CancellationToken.None);

        await Bus.SendAsync(queue, new Payload("c", 1),
            new SendOptions(
                CorrelationId: "corr-xyz",
                Properties: new Dictionary<string, string> { ["cho.kind"] = "test" }));

        var ctx = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("corr-xyz", ctx.CorrelationId);
        Assert.True(ctx.DeliveryCount >= 1);
        Assert.Equal("test", ctx.Properties["cho.kind"]);
        Assert.False(string.IsNullOrEmpty(ctx.MessageId));
    }

    [SkippableFact]
    public async Task SendAsync_MessageIdDedupIsIdempotent()
    {
        var queue = QueueNameFor("dedup");
        var count = 0;
        var gate = new TaskCompletionSource();
        await using var sub = Bus.Subscribe<Payload>(queue, (_, _, _) =>
        {
            Interlocked.Increment(ref count);
            gate.TrySetResult();
            return Task.CompletedTask;
        });
        await sub.StartAsync(CancellationToken.None);

        var id = "dedup-key-1";
        await Bus.SendAsync(queue, new Payload("one", 1), new SendOptions(MessageId: id));
        await Bus.SendAsync(queue, new Payload("two", 2), new SendOptions(MessageId: id));

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(200); // allow any second delivery to race in
        Assert.Equal(1, count);
    }

    [SkippableFact]
    public async Task ScheduleAsync_DeliversAfterDelay()
    {
        var queue = QueueNameFor("sched");
        var tcs = new TaskCompletionSource<DateTimeOffset>();
        await using var sub = Bus.Subscribe<Payload>(queue, (_, _, _) =>
        {
            tcs.TrySetResult(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        await sub.StartAsync(CancellationToken.None);

        var enqueueAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
        await Bus.ScheduleAsync(queue, new Payload("later", 7), enqueueAt);

        var deliveredAt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(deliveredAt >= enqueueAt.AddMilliseconds(-100),
            $"delivered too early: delivered={deliveredAt:o} enqueueAt={enqueueAt:o}");
    }

    [SkippableFact]
    public async Task Subscription_StopPreventsFurtherDispatch()
    {
        var queue = QueueNameFor("stop");
        var received = 0;
        var sub = Bus.Subscribe<Payload>(queue, (_, _, _) =>
        {
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        });
        await sub.StartAsync(CancellationToken.None);
        await sub.StopAsync(CancellationToken.None);
        await sub.DisposeAsync();

        await Bus.SendAsync(queue, new Payload("ignored", 0));
        await Task.Delay(200);
        Assert.Equal(0, received);
    }

    [SkippableFact]
    public async Task SendAsync_PreservesActivityParentAcrossBus()
    {
        var queue = QueueNameFor("trace");
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId? handlerTraceId = null;
        string? handlerParentSpanId = null;
        var gate = new TaskCompletionSource();
        await using var sub = Bus.Subscribe<Payload>(queue, (_, _, _) =>
        {
            handlerTraceId = Activity.Current?.TraceId;
            handlerParentSpanId = Activity.Current?.ParentSpanId.ToString();
            gate.TrySetResult();
            return Task.CompletedTask;
        });
        await sub.StartAsync(CancellationToken.None);

        using var outer = ChoActivitySource.Instance.StartActivity("outer", ActivityKind.Internal);
        Assert.NotNull(outer);
        var outerTraceId = outer!.TraceId;

        await Bus.SendAsync(queue, new Payload("traced", 0));

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(outerTraceId, handlerTraceId);
        Assert.False(string.IsNullOrEmpty(handlerParentSpanId));
        Assert.NotEqual("0000000000000000", handlerParentSpanId);
    }

    protected record Payload(string Name, int Value);
}
