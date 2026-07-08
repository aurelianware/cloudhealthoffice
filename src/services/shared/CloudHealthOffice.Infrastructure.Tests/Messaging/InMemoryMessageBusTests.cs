using CloudHealthOffice.Infrastructure.Messaging;

namespace CloudHealthOffice.Infrastructure.Tests.Messaging;

public class InMemoryMessageBusTests : MessageBusContractTests
{
    protected override ValueTask<IMessageBus> CreateBusAsync()
        => ValueTask.FromResult<IMessageBus>(new InMemoryMessageBus());

    [Fact]
    public async Task Subscribe_HonorsMaxConcurrentCalls()
    {
        await using var bus = new InMemoryMessageBus();
        var queue = $"parallel-{Guid.NewGuid():N}";
        var entered = 0;
        var maxObserved = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = bus.Subscribe<ParallelPayload>(
            queue,
            async (_, _, ct) =>
            {
                var current = Interlocked.Increment(ref entered);
                try
                {
                    UpdateMax(ref maxObserved, current);
                    if (current == 4)
                    {
                        allStarted.TrySetResult();
                    }

                    await release.Task.WaitAsync(ct);
                }
                finally
                {
                    Interlocked.Decrement(ref entered);
                }
            },
            new SubscriptionOptions(MaxConcurrentCalls: 4));
        await sub.StartAsync(CancellationToken.None);

        for (var i = 0; i < 4; i++)
        {
            await bus.SendAsync(queue, new ParallelPayload(i));
        }

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.Equal(4, maxObserved);
    }

    [Fact]
    public async Task Subscribe_IgnoresMessagesThatDoNotMatchRequiredProperties()
    {
        await using var bus = new InMemoryMessageBus();
        var queue = $"filtered-{Guid.NewGuid():N}";
        var received = System.Threading.Channels.Channel.CreateUnbounded<string>();

        await using var sub = bus.Subscribe<FilteredPayload>(
            queue,
            async (msg, _, ct) => await received.Writer.WriteAsync(msg.Value, ct),
            new SubscriptionOptions(
                RequiredProperties: new Dictionary<string, string>
                {
                    ["MessageType"] = "Wanted"
                }));
        await sub.StartAsync(CancellationToken.None);

        await bus.SendAsync(
            queue,
            new FilteredPayload("ignored"),
            new SendOptions(Properties: new Dictionary<string, string>
            {
                ["MessageType"] = "Other"
            }));
        await bus.SendAsync(
            queue,
            new FilteredPayload("wanted"),
            new SendOptions(Properties: new Dictionary<string, string>
            {
                ["MessageType"] = "Wanted"
            }));

        var value = await received.Reader.ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("wanted", value);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            received.Reader.ReadAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(250)));
    }

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private sealed record ParallelPayload(int Value);
    private sealed record FilteredPayload(string Value);
}
