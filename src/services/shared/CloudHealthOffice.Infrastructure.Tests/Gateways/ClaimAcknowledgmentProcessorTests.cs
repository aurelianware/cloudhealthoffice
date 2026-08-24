using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimAcknowledgmentProcessorTests
{
    private static ClaimTransmissionRecord Seed(
        IClaimTransmissionStore store,
        string submissionId = "synthetic-sub-001",
        string claimId = "CLM-P-1001",
        string tenant = "tenant-alpha",
        string gateway = "Stedi")
    {
        var record = new ClaimTransmissionRecord
        {
            TenantId = tenant,
            ClaimId = claimId,
            ClaimVersion = 1,
            GatewayName = gateway,
            ClaimType = GatewayClaimType.Professional,
            TransactionType = HealthcareTransactionType.ProfessionalClaim837P,
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = $"{tenant}|{claimId}|1|Professional|1",
            SubmissionId = submissionId,
            ExternalTransactionId = submissionId,
            PatientControlNumber = claimId,
            PayerId = "60054",
            SubmittedAtUtc = DateTimeOffset.Parse("2026-01-15T12:00:00Z"),
            CompletedAtUtc = DateTimeOffset.Parse("2026-01-15T12:00:01Z")
        };
        store.SaveAsync(record).GetAwaiter().GetResult();
        return record;
    }

    private static GatewayClaimAcknowledgment Accepted(
        ClaimTransmissionRecord tx, string ackId = "synthetic-ack-001") =>
        new()
        {
            AcknowledgmentId = ackId,
            Gateway = tx.GatewayName,
            OriginalSubmissionId = tx.SubmissionId,
            ClaimId = tx.ClaimId,
            ReceivedAt = DateTimeOffset.Parse("2026-01-15T12:05:00Z"),
            Status = ClaimAcknowledgmentStatus.Accepted,
            PatientControlNumber = tx.PatientControlNumber,
            ClaimControlNumber = "synthetic-pcn-001",
            ExternalTransactionId = ackId
        };

    private static ClaimAcknowledgmentProcessor Processor(
        IClaimTransmissionStore store,
        IClaimAcknowledgmentStore acks,
        IMessageBus? bus = null) =>
        new(acks, store, NullLogger<ClaimAcknowledgmentProcessor>.Instance, bus);

    [Fact]
    public async Task MatchesByStediSubmissionId_AndDoesNotMarkAdjudicatedOrPaid()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);
        var submittedAt = tx.SubmittedAtUtc;

        var result = await Processor(store, acks).ProcessAsync(Accepted(tx));

        result.Replay.Should().BeFalse();
        result.Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
        result.TenantId.Should().Be("tenant-alpha");
        result.TransmissionId.Should().Be(tx.TransmissionId);
        result.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);

        var updated = await store.GetByIdAsync(tx.TransmissionId);
        updated!.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        updated.SubmittedAtUtc.Should().Be(submittedAt);
        updated.SubmissionId.Should().Be("synthetic-sub-001");
        updated.Status.Should().NotBe(GatewayClaimTransmissionStatus.Failed);
        Enum.GetNames<GatewayClaimTransmissionStatus>().Should().NotContain("Adjudicated");
        Enum.GetNames<GatewayClaimTransmissionStatus>().Should().NotContain("Paid");
        updated.PayerId.Should().Be("60054");
    }

    [Fact]
    public async Task MatchesByPatientControlNumber_WhenSubmissionIdAbsent()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);

        var ack = Accepted(tx);
        ack.OriginalSubmissionId = null;
        ack.ClaimLevelResults.Add(new GatewayClaimAcknowledgmentClaimResult
        {
            Status = ClaimAcknowledgmentStatus.Accepted,
            PatientControlNumber = "CLM-P-1001"
        });

        var result = await Processor(store, acks).ProcessAsync(ack);
        result.TransmissionId.Should().Be(tx.TransmissionId);
        result.Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
    }

    [Fact]
    public async Task UnknownTransmission_IsUnableToMatch_AndIsPersisted()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();

        var result = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "orphan-ack",
            Gateway = "Stedi",
            OriginalSubmissionId = "does-not-exist",
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        result.Status.Should().Be(ClaimAcknowledgmentStatus.UnableToMatch);
        result.TransmissionId.Should().BeNull();
        result.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatchTransmission);
        var stored = await acks.GetByIdempotencyKeyAsync("Stedi", "orphan-ack");
        stored.Should().NotBeNull();
        stored!.UnmatchedReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AmbiguousSubmissionId_DoesNotAttachToEitherTransmission()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        Seed(store, tenant: "tenant-alpha", claimId: "CLM-A");
        Seed(store, tenant: "tenant-beta", claimId: "CLM-B");

        var result = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "amb-1",
            Gateway = "Stedi",
            OriginalSubmissionId = "synthetic-sub-001",
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        result.Status.Should().Be(ClaimAcknowledgmentStatus.UnableToMatch);
        (await store.GetByIdempotencyKeyAsync("tenant-alpha", "tenant-alpha|CLM-A|1|Professional|1"))!
            .Status.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
        (await store.GetByIdempotencyKeyAsync("tenant-beta", "tenant-beta|CLM-B|1|Professional|1"))!
            .Status.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
    }

    [Fact]
    public async Task Rejected_InvalidSubscriber_DoesNotAdjudicateOrPay()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store, submissionId: "synthetic-sub-002", claimId: "CLM-P-1002");

        var result = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "synthetic-ack-rej",
            Gateway = "Stedi",
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Rejected,
            ReceivedAt = DateTimeOffset.UtcNow,
            Errors =
            {
                new GatewayClaimAcknowledgmentIssue
                {
                    CategoryCode = "A3",
                    StatusCode = "164",
                    Description = "Entity's contract/member number.",
                    EntityCode = "IL",
                    Category = ClaimAcknowledgmentErrorCategory.InvalidSubscriber
                }
            }
        });

        result.Status.Should().Be(ClaimAcknowledgmentStatus.Rejected);
        result.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentRejected);
        var updated = await store.GetByIdAsync(tx.TransmissionId);
        updated!.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentRejected);
        updated.SubmittedAtUtc.Should().Be(tx.SubmittedAtUtc);
    }

    [Fact]
    public async Task PartialAndWarnings_MapToDistinctTransmissionStates()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);

        var partial = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-partial",
            Gateway = "Stedi",
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Partial,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        partial.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentPartial);
    }

    [Fact]
    public async Task Malformed_DoesNotChangeTransmission()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);

        var result = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-bad",
            Gateway = "Stedi",
            OriginalSubmissionId = tx.SubmissionId,
            TransmissionId = tx.TransmissionId,
            Status = ClaimAcknowledgmentStatus.Malformed,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        result.Status.Should().Be(ClaimAcknowledgmentStatus.Malformed);
        (await store.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
    }

    [Fact]
    public async Task ServiceLineResults_ArePersistedWithControlNumbers()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);

        await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-lines",
            Gateway = "Stedi",
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Rejected,
            ReceivedAt = DateTimeOffset.UtcNow,
            ServiceLineResults =
            {
                new GatewayClaimAcknowledgmentLineResult
                {
                    Status = ClaimAcknowledgmentLineStatus.LineRejected,
                    LineItemControlNumber = "1",
                    LineNumber = 1
                },
                new GatewayClaimAcknowledgmentLineResult
                {
                    Status = ClaimAcknowledgmentLineStatus.LineAccepted,
                    LineItemControlNumber = "2",
                    LineNumber = 2
                }
            }
        });

        var stored = await acks.GetByIdempotencyKeyAsync("Stedi", "ack-lines");
        stored!.ServiceLineResults.Should().HaveCount(2);
        stored.ServiceLineResults[0].LineItemControlNumber.Should().Be("1");
        stored.ServiceLineResults[1].LineNumber.Should().Be(2);
    }

    [Fact]
    public async Task DuplicateProcessing_IsIdempotent_AndDoesNotRepublish()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var bus = new CapturingMessageBus();
        var tx = Seed(store);
        var processor = Processor(store, acks, bus);
        var ack = Accepted(tx);

        var first = await processor.ProcessAsync(ack);
        var second = await processor.ProcessAsync(ack);
        var third = await processor.ProcessAsync(ack);

        first.Replay.Should().BeFalse();
        second.Replay.Should().BeTrue();
        third.Replay.Should().BeTrue();
        second.EventsPublished.Should().BeFalse();
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(1);
        bus.Sent.Count(s => s.Options?.Properties?[ClaimAcknowledgmentEventTopics.MessageTypeProperty]
            == ClaimAcknowledgmentMessageTypes.Accepted).Should().Be(1);
        bus.Sent.Count(s => s.Options?.Properties?[ClaimAcknowledgmentEventTopics.MessageTypeProperty]
            == ClaimAcknowledgmentMessageTypes.Received).Should().Be(1);
    }

    [Fact]
    public async Task DuplicateEventId_IsReplayWithoutRetrieveSideEffects()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);
        var processor = Processor(store, acks);
        var ack = Accepted(tx);
        ack.EventId = "evt-1";

        await processor.ProcessAsync(ack);
        ack.AcknowledgmentId = "different-ack-same-event";
        var replay = await processor.ProcessAsync(ack);
        replay.Replay.Should().BeTrue();
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task TenantComesFromTransmission_NotInboundPayload()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store, tenant: "tenant-alpha");

        var ack = Accepted(tx);
        ack.ClaimId = "forged-other-tenant-claim";

        var result = await Processor(store, acks).ProcessAsync(ack);
        result.TenantId.Should().Be("tenant-alpha");
        var stored = await acks.GetByIdempotencyKeyAsync("Stedi", ack.AcknowledgmentId);
        stored!.TenantId.Should().Be("tenant-alpha");
        stored.ClaimId.Should().Be("CLM-P-1001");
    }

    [Fact]
    public async Task ExplicitTransmissionId_DevInjection_UsesSameProcessor()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store, gateway: MockHealthcareGateway.GatewayName, submissionId: "mock-1");

        var result = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "dev-ack",
            Gateway = MockHealthcareGateway.GatewayName,
            TransmissionId = tx.TransmissionId,
            Status = ClaimAcknowledgmentStatus.Accepted,
            ClaimControlNumber = "synthetic-pcn-001",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        result.TransmissionId.Should().Be(tx.TransmissionId);
        result.TenantId.Should().Be("tenant-alpha");
        result.Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
    }

    [Fact]
    public async Task MatchesByCorrelationId_WhenSubmissionIdAbsent()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var tx = Seed(store);
        tx.CorrelationId = "corr-claim-1";
        await store.SaveAsync(tx);

        var result = await Processor(store, acks).ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-corr",
            Gateway = "Stedi",
            CorrelationId = "corr-claim-1",
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        result.TransmissionId.Should().Be(tx.TransmissionId);
        result.Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
    }

    [Fact]
    public async Task Replay_RetriesUnpublishedEvents()
    {
        var store = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var bus = new FailThenCaptureMessageBus(failFirstSends: 1);
        var tx = Seed(store);
        var processor = Processor(store, acks, bus);

        var first = await processor.ProcessAsync(Accepted(tx));
        first.EventsPublished.Should().BeFalse();
        bus.Sent.Should().BeEmpty();

        var second = await processor.ProcessAsync(Accepted(tx));
        second.Replay.Should().BeTrue();
        bus.Sent.Should().NotBeEmpty();
        (await acks.GetByIdempotencyKeyAsync("Stedi", "synthetic-ack-001"))!
            .EventsPublished.Should().BeTrue();
    }
}
