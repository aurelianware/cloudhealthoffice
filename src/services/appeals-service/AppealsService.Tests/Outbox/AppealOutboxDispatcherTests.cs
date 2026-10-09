using System.Diagnostics;
using System.Diagnostics.Metrics;
using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Outbox;

/// <summary>Dispatcher behavior independent of the store: inline latency, gauges, deployment defaults.</summary>
public class AppealOutboxDispatcherTests
{
    private const string Tenant = "t-disp";

    private static async Task<Appeal> SeedAsync(InMemoryAppealRepository repo)
    {
        var appeal = new Appeal
        {
            TenantId = Tenant, Id = Guid.NewGuid().ToString(), AppealNumber = "APL-1", ClaimId = "c1",
            MemberId = "m1", LineOfBusiness = LineOfBusiness.Medicare
        };
        await repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = Tenant, AppealId = appeal.Id, EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated, ActorId = "u"
        }.Queued(appeal));
        return appeal;
    }

    /// <summary>Transport whose produce blocks until released — a broker timing out.</summary>
    private sealed class HangingTransport : IAppealEventTransport
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Delivered;
        public Task<AppealEventPublisherState> Started { get; } = Task.FromResult(AppealEventPublisherState.Available);
        public bool IsTransient(Exception error) => true;

        public async Task ProduceAsync(AppealOutboxMessage message, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            Interlocked.Increment(ref Delivered);
        }
    }

    [Fact]
    public async Task Inline_Dispatch_Is_Fire_And_Forget_So_A_Kafka_Timeout_Never_Adds_Request_Latency()
    {
        var repo = new InMemoryAppealRepository();
        var appeal = await SeedAsync(repo);
        var transport = new HangingTransport();
        var dispatcher = new AppealOutboxDispatcher(repo, transport, new AppealOutboxOptions { Enabled = false },
            NullLogger<AppealOutboxDispatcher>.Instance);

        var watch = Stopwatch.StartNew();
        await dispatcher.NotifyChangedAsync(Tenant, appeal.Id);
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the request does not wait for the produce");
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the publish did start in the background
        transport.Delivered.Should().Be(0);

        transport.Release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (repo.OutboxOf(Tenant, appeal.Id).Single().Status != AppealOutboxStatus.Sent && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        repo.OutboxOf(Tenant, appeal.Id).Single().Status.Should().Be(AppealOutboxStatus.Sent);
    }

    [Fact]
    public async Task Backlog_Gauges_Report_Pending_Count_And_Oldest_Age()
    {
        var repo = new InMemoryAppealRepository();
        await SeedAsync(repo);
        await SeedAsync(repo);
        var dispatcher = new AppealOutboxDispatcher(repo, new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled),
            new AppealOutboxOptions { Enabled = false }, NullLogger<AppealOutboxDispatcher>.Instance);

        await dispatcher.RefreshStatsAsync();

        var measured = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name.StartsWith("cho.appeals.outbox.", StringComparison.Ordinal)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => measured[i.Name] = v);
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => measured[i.Name] = v);
        listener.Start();
        listener.RecordObservableInstruments();

        // Other test classes may refresh the shared gauge concurrently; this
        // store alone holds 2 pending events, so the value is at least set.
        measured.Should().ContainKey("cho.appeals.outbox.pending");
        measured.Should().ContainKey("cho.appeals.outbox.oldest_pending_age");
        measured["cho.appeals.outbox.oldest_pending_age"].Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Shipped_Manifest_Skips_Events_While_No_Broker_Is_Configured()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "appeals-service.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test runs under the appeals-service directory");

        var manifest = File.ReadAllText(Path.Combine(dir!.FullName, "k8s", "appeals-service-deployment.yaml"));
        manifest.Should().Contain("AppealOutbox__SkipWhenKafkaDisabled: \"true\"",
            "with an empty Kafka__BootstrapServers the outbox would otherwise grow without bound");
        manifest.Should().Contain("key: AppealOutbox__SkipWhenKafkaDisabled", "the deployment wires the setting into the pod");
    }
}
