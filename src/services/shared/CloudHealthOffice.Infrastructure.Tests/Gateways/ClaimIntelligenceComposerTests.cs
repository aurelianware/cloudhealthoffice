using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimIntelligenceComposerTests
{
    [Fact]
    public async Task SubmittedAcceptedInProcess_Without835_IsProcessing()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted);
        await InquireAsync(harness, GatewayClaimStatus.InProcess);

        var view = await harness.Composer.ComposeAsync(Request());

        view.Should().NotBeNull();
        view!.LifecycleStatus.Should().Be(ClaimIntelligenceLifecycleStatus.Processing);
        view.Transactions.Submission!.Status.Should().Be(nameof(GatewayClaimTransmissionStatus.AcknowledgmentAccepted));
        view.Transactions.Acknowledgment!.Status.Should().Be(nameof(ClaimAcknowledgmentStatus.Accepted));
        view.Transactions.Status!.Status.Should().Be(nameof(GatewayClaimStatus.InProcess));
        view.Transactions.Remittance.Should().BeNull();
        view.Financial.HasRemittance.Should().BeFalse();
        view.Workflow.NextAction.Should().Be(ClaimIntelligenceNextAction.WaitForPayer);
        view.Signals.MissingTransactionLinks.Should().Contain("835");
    }

    [Fact]
    public async Task Paid835_IsPaidAndDoesNotChange277ca()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted, "PAYER-CCN-9");
        await RemitAsync(harness, paid: 320m, charged: 500m, patient: 80m);

        var view = await harness.Composer.ComposeAsync(Request());

        view!.LifecycleStatus.Should().Be(ClaimIntelligenceLifecycleStatus.Paid);
        view.Transactions.Acknowledgment!.Status.Should().Be(nameof(ClaimAcknowledgmentStatus.Accepted));
        view.Transactions.Remittance!.Status.Should().Be(nameof(RemittanceLifecycleStatus.AvailableForPosting));
        view.Financial.HasRemittance.Should().BeTrue();
        view.Financial.SubmittedAmount.Should().Be(500m);
        view.Financial.PaidAmount.Should().Be(320m);
        view.Financial.PatientResponsibility.Should().Be(80m);
        view.Workflow.NextAction.Should().Be(ClaimIntelligenceNextAction.ReadyForPosting);
        view.Workflow.PatientResponsibilityDisplay.Should().Be("80");
        view.Timeline.Should().Contain(e =>
            e.SourceTransaction == "835" && e.EventType == "ReadyForPosting");
        (await harness.Acks.ListByTransmissionIdAsync(harness.Transmission.TransmissionId))
            .Single().Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
        (await harness.Transmissions.GetByIdAsync(harness.Transmission.TransmissionId))!
            .Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
    }

    [Fact]
    public async Task Posted835_ClearsReadyForPostingWithoutChanging277ca()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted, "PAYER-CCN-9");
        await RemitAsync(harness, paid: 320m, charged: 500m, patient: 80m);
        var stored = (await harness.Remittances.ListByTransmissionIdAsync(
            harness.Transmission.TransmissionId)).Single();
        await new RemittancePoster(
            harness.Remittances,
            harness.Transmissions,
            new InMemoryClaimRemittancePostingSink(),
            new InMemoryRemittanceAccumulatorSink(),
            NullLogger<RemittancePoster>.Instance)
            .PostAsync(new RemittancePostRequest
            {
                ReceiptId = stored.ReceiptId,
                TenantId = "tenant-alpha"
            });

        var view = await harness.Composer.ComposeAsync(Request());
        view!.Transactions.Remittance!.Status.Should().Be(nameof(RemittanceLifecycleStatus.Posted));
        view.Workflow.NextAction.Should().Be(ClaimIntelligenceNextAction.None);
        view.Timeline.Single(e => e.SourceTransaction == "835").EventId.Should().Be(
            $"835:{stored.ReceiptId}");
        view.Timeline.Single(e => e.SourceTransaction == "835").EventType.Should().Be("Posted");
        (await harness.Acks.ListByTransmissionIdAsync(harness.Transmission.TransmissionId))
            .Single().Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
        (await harness.Transmissions.GetByIdAsync(harness.Transmission.TransmissionId))!
            .Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
    }

    [Fact]
    public async Task Partial835_IsPartiallyPaid()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted, "PAYER-CCN-9");
        await RemitAsync(harness, paid: 100m, charged: 500m, patient: 50m, claimStatusCode: "2");

        var view = await harness.Composer.ComposeAsync(Request());
        view!.LifecycleStatus.Should().Be(ClaimIntelligenceLifecycleStatus.PartiallyPaid);
    }

    [Fact]
    public async Task InboundAttachment_SetsAttachmentAvailable()
    {
        var harness = await SeedSubmittedAsync();
        await harness.Inbound.SaveAsync(new InboundClaimAttachmentReceipt
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-INT-1001",
            AttachmentType = ClaimAttachmentType.DentalImage,
            Mode = ClaimAttachmentMode.Unsolicited,
            Status = InboundClaimAttachmentStatus.AvailableToClaim,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            SourceAdapter = "test"
        });

        var view = await harness.Composer.ComposeAsync(Request());
        view!.Attachments.AttachmentAvailable.Should().BeTrue();
        view.Attachments.Received.Should().BeTrue();
        view.Attachments.InboundCount.Should().Be(1);
        view.Attachments.Types.Should().Contain("DentalImage");
        view.Timeline.Should().Contain(e => e.EventType == "275AttachmentReceived");
    }

    [Fact]
    public async Task TenantIsolation_DoesNotReturnOtherTenant()
    {
        var harness = await SeedSubmittedAsync();
        var other = await harness.Composer.ComposeAsync(new ClaimIntelligenceRequest
        {
            TenantId = "tenant-beta",
            ClaimId = "CLM-INT-1001"
        });
        other.Should().BeNull();
    }

    [Fact]
    public async Task TimelineEventIds_StayStableWhenSourceStatusChanges()
    {
        var harness = await SeedSubmittedAsync();
        var first = await harness.Composer.ComposeAsync(Request());
        var submittedId = first!.Timeline.Single(e => e.SourceTransaction == "837").EventId;

        harness.Transmission.Status = GatewayClaimTransmissionStatus.AcknowledgmentAccepted;
        await harness.Transmissions.SaveAsync(harness.Transmission);
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted, "PAYER-CCN-9");
        await RemitAsync(harness, paid: 320m, charged: 500m, patient: 80m);

        var second = await harness.Composer.ComposeAsync(Request());
        second!.Timeline.Single(e => e.SourceTransaction == "837").EventId.Should().Be(submittedId);
        second.Timeline.Single(e => e.SourceTransaction == "837").EventId.Should().Be(
            $"837:{harness.Transmission.TransmissionId}");
        second.Timeline.Single(e => e.SourceTransaction == "277CA").EventId.Should().StartWith("277ca:");
        second.Timeline.Single(e => e.SourceTransaction == "277CA").EventId.Should().NotContain(
            nameof(ClaimAcknowledgmentStatus.Accepted));
        second.Timeline.Single(e => e.SourceTransaction == "835").EventId.Should().StartWith("835:");
        second.Timeline.Single(e => e.SourceTransaction == "835").EventId.Should().NotContain(
            nameof(RemittanceLifecycleStatus.AvailableForPosting));
        second.Timeline.Single(e => e.SourceTransaction == "837").Status
            .Should().Be(nameof(GatewayClaimTransmissionStatus.AcknowledgmentAccepted));
    }

    [Fact]
    public async Task Duplicate277caAnd835_DoNotDuplicateTimeline()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted, "PAYER-CCN-9");
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted, "PAYER-CCN-9");
        await RemitAsync(harness, paid: 320m, charged: 500m, patient: 80m);
        await RemitAsync(harness, paid: 320m, charged: 500m, patient: 80m);

        var view = await harness.Composer.ComposeAsync(Request());
        view!.Timeline.Count(e => e.SourceTransaction == "277CA").Should().Be(1);
        view.Timeline.Count(e => e.SourceTransaction == "835").Should().Be(1);
        (await harness.Acks.ListByTransmissionIdAsync(harness.Transmission.TransmissionId))
            .Should().ContainSingle();
        (await harness.Remittances.ListByTransmissionIdAsync(harness.Transmission.TransmissionId))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateInboundAttachment_DoesNotDuplicateTimeline()
    {
        var harness = await SeedSubmittedAsync();
        var receipt = new InboundClaimAttachmentReceipt
        {
            ReceiptId = "att-1",
            TenantId = "tenant-alpha",
            ClaimId = "CLM-INT-1001",
            AttachmentType = ClaimAttachmentType.DentalNarrative,
            Status = InboundClaimAttachmentStatus.AvailableToClaim,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            SourceAdapter = "test"
        };
        await harness.Inbound.SaveAsync(receipt);
        await harness.Inbound.SaveAsync(receipt);

        var view = await harness.Composer.ComposeAsync(Request());
        view!.Timeline.Count(e => e.EventType == "275AttachmentReceived").Should().Be(1);
        view.Attachments.Count.Should().Be(1);
    }

    [Fact]
    public async Task AcknowledgmentAccepted_IsNotPaid()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted);

        var view = await harness.Composer.ComposeAsync(Request());
        view!.LifecycleStatus.Should().Be(ClaimIntelligenceLifecycleStatus.AcceptedByPayer);
        view.LifecycleStatus.Should().NotBe(ClaimIntelligenceLifecycleStatus.Paid);
        view.Transactions.Remittance.Should().BeNull();
        view.Financial.HasRemittance.Should().BeFalse();
    }

    [Fact]
    public async Task ClaimStatusPaid_DoesNotCreate835()
    {
        var harness = await SeedSubmittedAsync();
        await AcknowledgeAsync(harness, ClaimAcknowledgmentStatus.Accepted);
        await InquireAsync(harness, GatewayClaimStatus.Paid);

        var view = await harness.Composer.ComposeAsync(Request());
        view!.LifecycleStatus.Should().Be(ClaimIntelligenceLifecycleStatus.Processing);
        view.Transactions.Status!.Status.Should().Be(nameof(GatewayClaimStatus.Paid));
        view.Transactions.Remittance.Should().BeNull();
        view.Financial.HasRemittance.Should().BeFalse();
        view.Signals.MissingTransactionLinks.Should().Contain("835");
    }

    [Fact]
    public async Task GatewayAcceptedWithout277ca_IsAcceptedByClearinghouse()
    {
        var harness = await SeedSubmittedAsync();
        var view = await harness.Composer.ComposeAsync(Request());
        view!.LifecycleStatus.Should().Be(ClaimIntelligenceLifecycleStatus.AcceptedByClearinghouse);
        view.Transactions.Acknowledgment.Should().BeNull();
        view.Workflow.NextAction.Should().Be(ClaimIntelligenceNextAction.WaitForClearinghouse);
    }

    [Fact]
    public async Task MissingTenantOrClaim_ReturnsNull()
    {
        var harness = await SeedSubmittedAsync();
        (await harness.Composer.ComposeAsync(new ClaimIntelligenceRequest
        {
            TenantId = "",
            ClaimId = "CLM-INT-1001"
        })).Should().BeNull();
        (await harness.Composer.ComposeAsync(new ClaimIntelligenceRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = ""
        })).Should().BeNull();
    }

    private static ClaimIntelligenceRequest Request() =>
        new() { TenantId = "tenant-alpha", ClaimId = "CLM-INT-1001" };

    private static async Task<Harness> SeedSubmittedAsync()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var inquiries = new InMemoryClaimStatusInquiryStore();
        var outbound = new InMemoryClaimAttachmentTransmissionStore();
        var inbound = new InMemoryInboundClaimAttachmentReceiptStore();
        var remittances = new InMemoryRemittanceStore();
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-INT-1001",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            SubmissionId = "synthetic-sub-int",
            PatientControlNumber = "CLM-INT-1001",
            PayerId = "60054",
            ClaimAmount = 500m,
            SubmittedAtUtc = DateTimeOffset.UtcNow,
            InquirySource = new ClaimStatusInquirySource
            {
                BillingProvider = new GatewayClaimProvider
                {
                    Npi = "1999999984",
                    OrganizationName = "Therapy Associates"
                },
                Subscriber = new GatewayEligibilityPerson
                {
                    MemberId = "U7777788888",
                    FirstName = "John",
                    LastName = "Anon"
                },
                ClaimAmount = 500m,
                ServiceLines =
                {
                    new ClaimStatusLineSource { LineNumber = 1, ProcedureCode = "D2740", ChargeAmount = 500m }
                }
            }
        };
        await transmissions.SaveAsync(tx);
        var composer = new ClaimIntelligenceComposer(
            transmissions, acks, inquiries, outbound, inbound, remittances,
            NullLogger<ClaimIntelligenceComposer>.Instance);
        return new Harness(composer, transmissions, acks, inquiries, outbound, inbound, remittances, tx);
    }

    private static async Task AcknowledgeAsync(
        Harness harness, ClaimAcknowledgmentStatus status, string? ccn = "ACK-CCN-1")
    {
        var processor = new ClaimAcknowledgmentProcessor(
            harness.Acks, harness.Transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-int-1",
            Gateway = "Stedi",
            TransmissionId = harness.Transmission.TransmissionId,
            OriginalSubmissionId = harness.Transmission.SubmissionId,
            Status = status,
            ClaimControlNumber = ccn,
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private static async Task InquireAsync(Harness harness, GatewayClaimStatus status)
    {
        await harness.Inquiries.SaveAsync(new ClaimStatusInquiryRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-INT-1001",
            TransmissionId = harness.Transmission.TransmissionId,
            GatewayName = "Stedi",
            NormalizedStatus = status,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static async Task RemitAsync(
        Harness harness, decimal paid, decimal charged, decimal patient, string claimStatusCode = "1")
    {
        var processor = new RemittanceProcessor(
            harness.Remittances, harness.Transmissions, NullLogger<RemittanceProcessor>.Instance);
        await processor.ProcessAsync(new GatewayRemittance
        {
            RemittanceId = "era-int-1",
            Gateway = "Stedi",
            PaymentAmount = paid,
            ReceivedAt = DateTimeOffset.UtcNow,
            Claims =
            {
                new RemittedClaim
                {
                    PayerClaimControlNumber = harness.Transmission.PayerClaimControlNumber ?? "PAYER-CCN-9",
                    PatientControlNumber = "CLM-INT-1001",
                    ChargedAmount = charged,
                    AllowedAmount = charged - 100m,
                    PaidAmount = paid,
                    PatientResponsibilityAmount = patient,
                    ClaimStatusCode = claimStatusCode
                }
            }
        });
    }

    private sealed record Harness(
        ClaimIntelligenceComposer Composer,
        InMemoryClaimTransmissionStore Transmissions,
        InMemoryClaimAcknowledgmentStore Acks,
        InMemoryClaimStatusInquiryStore Inquiries,
        InMemoryClaimAttachmentTransmissionStore Outbound,
        InMemoryInboundClaimAttachmentReceiptStore Inbound,
        InMemoryRemittanceStore Remittances,
        ClaimTransmissionRecord Transmission);
}
