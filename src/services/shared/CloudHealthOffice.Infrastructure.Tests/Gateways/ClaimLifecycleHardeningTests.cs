using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimLifecycleHardeningTests
{
    [Fact]
    public async Task ConcurrentProcessors_CreateOneAcknowledgmentAndOneTransition()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var bus = new CapturingMessageBus();
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = "k",
            SubmissionId = "synthetic-sub-001",
            PatientControlNumber = "CLM-P-1001",
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(tx);
        var processor = new ClaimAcknowledgmentProcessor(
            acks, store, NullLogger<ClaimAcknowledgmentProcessor>.Instance, bus);

        var tasks = Enumerable.Range(0, 12).Select(_ => processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-race",
            Gateway = "Stedi",
            OriginalSubmissionId = "synthetic-sub-001",
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        }));
        var results = await Task.WhenAll(tasks);

        results.Count(r => !r.Replay).Should().Be(1);
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(1);
        (await store.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        bus.Sent.Count(s => s.Options?.Properties?[ClaimAcknowledgmentEventTopics.MessageTypeProperty]
            == ClaimAcknowledgmentMessageTypes.Accepted).Should().Be(1);
    }

    [Fact]
    public async Task DispatchPending_PublishesPreviouslyFailedOutboxOnce()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var bus = new FailThenCaptureMessageBus(failFirstSends: 1);
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = "k",
            SubmissionId = "synthetic-sub-001",
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(tx);
        var processor = new ClaimAcknowledgmentProcessor(
            acks, store, NullLogger<ClaimAcknowledgmentProcessor>.Instance, bus);

        await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-outbox",
            Gateway = "Stedi",
            OriginalSubmissionId = "synthetic-sub-001",
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        (await acks.GetByIdempotencyKeyAsync("Stedi", "ack-outbox"))!.HasPendingOutbox.Should().BeTrue();

        await processor.DispatchPendingAsync();
        var stored = await acks.GetByIdempotencyKeyAsync("Stedi", "ack-outbox");
        stored!.EventsPublished.Should().BeTrue();
        stored.Outbox.Should().OnlyContain(e => e.PublishedAtUtc.HasValue);
        stored.LastError.Should().BeNull();
        stored.LastErrorCategory.Should().Be(GatewayErrorCategory.None);
        bus.Sent.Select(s => s.Options?.Properties?[ClaimAcknowledgmentEventTopics.MessageTypeProperty])
            .Distinct().Should().BeEquivalentTo(new[]
            {
                ClaimAcknowledgmentMessageTypes.Received,
                ClaimAcknowledgmentMessageTypes.Accepted
            });
    }

    [Fact]
    public async Task ConcurrentTransmissionTryCreate_OneWinner()
    {
        var store = new InMemoryClaimTransmissionStore();
        var seed = () => new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            IdempotencyKey = "tenant-alpha|CLM-P-1001|1|Professional|1",
            Status = GatewayClaimTransmissionStatus.Transmitting,
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => store.TryCreateAsync(seed())));
        results.Count(r => r.Created).Should().Be(1);
        results.Select(r => r.Record.TransmissionId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void StateMachine_RejectsBackwardAndMalformedOverwrite()
    {
        ClaimTransmissionStateMachine.TryTransition(
            GatewayClaimTransmissionStatus.AcknowledgmentAccepted,
            GatewayClaimTransmissionStatus.Transmitting,
            ClaimAcknowledgmentStatus.Accepted,
            out _).Should().BeFalse();

        ClaimTransmissionStateMachine.TryTransition(
            GatewayClaimTransmissionStatus.AcknowledgmentAccepted,
            GatewayClaimTransmissionStatus.AcknowledgmentFailed,
            ClaimAcknowledgmentStatus.Malformed,
            out var next).Should().BeFalse();
        next.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);

        ClaimTransmissionStateMachine.TryTransition(
            GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            GatewayClaimTransmissionStatus.AcknowledgmentRejected,
            ClaimAcknowledgmentStatus.Rejected,
            out var rejected).Should().BeTrue();
        rejected.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentRejected);
    }

    [Fact]
    public async Task Unmatched_DoesNotAssignTenant()
    {
        var processor = new ClaimAcknowledgmentProcessor(
            new InMemoryClaimAcknowledgmentStore(),
            new InMemoryClaimTransmissionStore(),
            NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        var result = await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "orphan",
            Gateway = "Stedi",
            OriginalSubmissionId = "nope",
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        result.Status.Should().Be(ClaimAcknowledgmentStatus.UnableToMatch);
        result.TenantId.Should().BeEmpty();
        result.TransmissionId.Should().BeNull();
    }

    [Fact]
    public async Task ProductionInMemory_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HealthcareTransactions:DefaultGateway"] = "Mock",
                ["HealthcareTransactions:ClaimLifecycle:Store"] = "InMemory"
            }).Build();
        services.AddChoHealthcareGateways(config);
        var sp = services.BuildServiceProvider();
        var guard = ActivatorUtilities.CreateInstance<ClaimLifecycleStoreGuard>(sp);
        var act = () => guard.StartAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Mongo");
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
