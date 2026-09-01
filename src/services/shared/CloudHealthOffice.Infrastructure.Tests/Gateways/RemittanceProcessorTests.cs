using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class RemittanceProcessorTests
{
    [Fact]
    public async Task PayerClaimControlNumber_MatchesWithoutChanging277caOrTransmissionStatus()
    {
        var (processor, transmissions, acks, receipts) = Create();
        var tx = await SeedTransmissionAsync(transmissions, payerCcn: "PAYER-CCN-9");
        await new ClaimAcknowledgmentProcessor(
            acks, transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance)
            .ProcessAsync(new GatewayClaimAcknowledgment
            {
                AcknowledgmentId = "ack-1",
                Gateway = "Stedi",
                TransmissionId = tx.TransmissionId,
                OriginalSubmissionId = tx.SubmissionId,
                Status = ClaimAcknowledgmentStatus.Accepted,
                ClaimControlNumber = "PAYER-CCN-9",
                ReceivedAt = DateTimeOffset.UtcNow
            });

        var result = await processor.ProcessAsync(PaidRemittance(payerCcn: "PAYER-CCN-9"));

        result.Status.Should().Be(RemittanceLifecycleStatus.AvailableForPosting);
        result.TenantId.Should().Be("tenant-alpha");
        result.MatchedClaimCount.Should().Be(1);
        (await transmissions.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Single().Status
            .Should().Be(ClaimAcknowledgmentStatus.Accepted);
        var stored = (await receipts.ListByTransmissionIdAsync(tx.TransmissionId)).Single();
        stored.Claims[0].PaidAmount.Should().Be(320m);
        stored.Claims[0].PatientResponsibilityAmount.Should().Be(80m);
        stored.Claims[0].Adjustments.Should().Contain(a => a.Kind == RemittanceAdjustmentKind.Deductible);
    }

    [Fact]
    public async Task PatientControlNumber_MatchesWhenPayerControlMissing()
    {
        var (processor, transmissions, _, _) = Create();
        await SeedTransmissionAsync(transmissions);
        var result = await processor.ProcessAsync(PaidRemittance(payerCcn: null, patient: "CLM-P-1001"));
        result.Status.Should().Be(RemittanceLifecycleStatus.AvailableForPosting);
        result.MatchedClaimCount.Should().Be(1);
    }

    [Fact]
    public async Task UnknownClaim_IsUnmatched()
    {
        var (processor, _, _, receipts) = Create();
        var result = await processor.ProcessAsync(PaidRemittance(payerCcn: "NOPE", patient: "NOPE"));
        result.Status.Should().Be(RemittanceLifecycleStatus.Unmatched);
        result.TenantId.Should().BeEmpty();
        (await receipts.GetByIdempotencyKeyAsync("Stedi", "era-1"))!.Status
            .Should().Be(RemittanceLifecycleStatus.Unmatched);
    }

    [Fact]
    public async Task AmbiguousPatientControl_IsUnmatched()
    {
        var (processor, transmissions, _, _) = Create();
        await SeedTransmissionAsync(transmissions, claimId: "CLM-A", pcn: "CLM-P-1001");
        await SeedTransmissionAsync(transmissions, claimId: "CLM-B", pcn: "CLM-P-1001");
        var result = await processor.ProcessAsync(PaidRemittance(payerCcn: null, patient: "CLM-P-1001"));
        result.Status.Should().Be(RemittanceLifecycleStatus.Unmatched);
        result.ErrorCategory.Should().Be(GatewayErrorCategory.AmbiguousClaim);
    }

    [Fact]
    public async Task MixedTenants_FailClosedWithoutAssignment()
    {
        var (processor, transmissions, _, _) = Create();
        await SeedTransmissionAsync(transmissions, claimId: "CLM-A", payerCcn: "CCN-A");
        var other = await SeedTransmissionAsync(transmissions, claimId: "CLM-B", payerCcn: "CCN-B");
        other.TenantId = "tenant-beta";
        await transmissions.SaveAsync(other);

        var remittance = PaidRemittance();
        remittance.Claims =
        [
            new RemittedClaim { PayerClaimControlNumber = "CCN-A", ChargedAmount = 10, PaidAmount = 8 },
            new RemittedClaim { PayerClaimControlNumber = "CCN-B", ChargedAmount = 10, PaidAmount = 8 }
        ];
        var result = await processor.ProcessAsync(remittance);
        result.Status.Should().Be(RemittanceLifecycleStatus.Failed);
        result.TenantId.Should().BeEmpty();
        result.ErrorCategory.Should().Be(GatewayErrorCategory.AmbiguousClaim);
    }

    [Fact]
    public async Task DuplicateEra_IsReplay()
    {
        var (processor, transmissions, _, _) = Create();
        await SeedTransmissionAsync(transmissions, payerCcn: "PAYER-CCN-9");
        var first = await processor.ProcessAsync(PaidRemittance(payerCcn: "PAYER-CCN-9"));
        var second = await processor.ProcessAsync(PaidRemittance(payerCcn: "PAYER-CCN-9"));
        first.Replay.Should().BeFalse();
        second.Replay.Should().BeTrue();
        second.RemittanceId.Should().Be(first.RemittanceId);
    }

    [Fact]
    public async Task EmptyClaims_AreFailedNotPosted()
    {
        var (processor, _, _, _) = Create();
        var result = await processor.ProcessAsync(new GatewayRemittance
        {
            RemittanceId = "empty",
            Gateway = "Stedi",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        result.Status.Should().Be(RemittanceLifecycleStatus.Failed);
        result.ErrorCategory.Should().Be(GatewayErrorCategory.MalformedResponse);
    }

    [Fact]
    public async Task ClaimIdOnly_DoesNotMatchPatientControlNumber()
    {
        var (processor, transmissions, _, _) = Create();
        await SeedTransmissionAsync(transmissions, claimId: "CLM-P-1001", pcn: "CLM-P-1001");
        var result = await processor.ProcessAsync(new GatewayRemittance
        {
            RemittanceId = "era-claim-id-only",
            Gateway = "Stedi",
            ReceivedAt = DateTimeOffset.UtcNow,
            Claims =
            {
                new RemittedClaim { ClaimId = "CLM-P-1001", ChargedAmount = 10, PaidAmount = 8 }
            }
        });
        result.Status.Should().Be(RemittanceLifecycleStatus.Unmatched);
        result.MatchedClaimCount.Should().Be(0);
        result.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatch);
    }

    [Fact]
    public async Task RetrieveFailure_PersistsOriginalErrorCategory()
    {
        var (processor, _, _, receipts) = Create();
        var result = await processor.ProcessAsync(new GatewayRemittance
        {
            RemittanceId = "era-auth",
            Gateway = "Stedi",
            ReceivedAt = DateTimeOffset.UtcNow,
            ErrorCategory = GatewayErrorCategory.Authentication,
            ErrorMessage = "api-key-rejected"
        });
        result.Status.Should().Be(RemittanceLifecycleStatus.Failed);
        result.ErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
        result.ErrorMessage.Should().Be("api-key-rejected");
        var stored = await receipts.GetByIdempotencyKeyAsync("Stedi", "era-auth");
        stored!.LastErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
        stored.UnmatchedReason.Should().Be("retrieve-failed");
    }

    [Fact]
    public async Task Clone_IsolatesNestedClaimMutations()
    {
        var store = new InMemoryRemittanceStore();
        var receipt = new RemittanceReceipt
        {
            Gateway = "Stedi",
            RemittanceId = "era-clone",
            Claims =
            {
                new RemittedClaim
                {
                    PaidAmount = 10m,
                    Adjustments = { new RemittanceAdjustment { Amount = 1m } },
                    ServiceLines =
                    {
                        new RemittedServiceLine
                        {
                            PaidAmount = 10m,
                            Adjustments = { new RemittanceAdjustment { Amount = 2m } }
                        }
                    }
                }
            }
        };
        await store.SaveAsync(receipt);
        receipt.Claims[0].PaidAmount = 999m;
        receipt.Claims[0].Adjustments[0].Amount = 999m;
        receipt.Claims[0].ServiceLines[0].PaidAmount = 999m;
        receipt.Claims[0].ServiceLines[0].Adjustments[0].Amount = 999m;

        var loaded = await store.GetByIdAsync(receipt.ReceiptId);
        loaded!.Claims[0].PaidAmount.Should().Be(10m);
        loaded.Claims[0].Adjustments[0].Amount.Should().Be(1m);
        loaded.Claims[0].ServiceLines[0].PaidAmount.Should().Be(10m);
        loaded.Claims[0].ServiceLines[0].Adjustments[0].Amount.Should().Be(2m);

        loaded.Claims[0].PaidAmount = 50m;
        var reloaded = await store.GetByIdAsync(receipt.ReceiptId);
        reloaded!.Claims[0].PaidAmount.Should().Be(10m);
    }

    [Fact]
    public async Task Outbox_FirstPublish_SetsReplayFalse()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        await SeedTransmissionAsync(transmissions, payerCcn: "PAYER-CCN-9");
        var receipts = new InMemoryRemittanceStore();
        var bus = new CapturingMessageBus();
        var processor = new RemittanceProcessor(
            receipts, transmissions, NullLogger<RemittanceProcessor>.Instance, bus);

        var result = await processor.ProcessAsync(PaidRemittance(payerCcn: "PAYER-CCN-9"));
        result.Replay.Should().BeFalse();
        bus.Sent.Should().NotBeEmpty();
        bus.Sent.Select(s => ((RemittanceReceivedMessage)s.Message).Replay)
            .Should().OnlyContain(replay => !replay);
    }

    [Fact]
    public async Task Outbox_SetsReplayFalseOnFirstPublishAndTrueOnRetry()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        await SeedTransmissionAsync(transmissions, payerCcn: "PAYER-CCN-9");
        var receipts = new InMemoryRemittanceStore();
        var bus = new FailThenCaptureMessageBus(failFirstSends: 2);
        var processor = new RemittanceProcessor(
            receipts, transmissions, NullLogger<RemittanceProcessor>.Instance, bus);

        var first = await processor.ProcessAsync(PaidRemittance(payerCcn: "PAYER-CCN-9"));
        first.Replay.Should().BeFalse();
        first.EventsPublished.Should().BeFalse();
        bus.Sent.Should().BeEmpty();

        var second = await processor.ProcessAsync(PaidRemittance(payerCcn: "PAYER-CCN-9"));
        second.Replay.Should().BeTrue();
        bus.Sent.Should().NotBeEmpty();
        bus.Sent.Select(s => s.Message).Should().AllBeOfType<RemittanceReceivedMessage>();
        bus.Sent.Select(s => ((RemittanceReceivedMessage)s.Message).Replay)
            .Should().OnlyContain(replay => replay);
    }

    [Fact]
    public async Task Ingress_NonTransientRetrieveFailure_StoresOriginalCategory()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var receipts = new InMemoryRemittanceStore();
        var processor = new RemittanceProcessor(
            receipts, transmissions, NullLogger<RemittanceProcessor>.Instance);
        var gateway = new StubRemittanceGateway
        {
            Next = GatewayResponse<GatewayRemittance>.Failure(
                "api-key-rejected",
                new GatewayTransactionMetadata
                {
                    GatewayName = "Stedi",
                    TransactionType = HealthcareTransactionType.Remittance835,
                    ErrorCategory = GatewayErrorCategory.Authentication
                })
        };
        var ingress = new RemittanceIngress(
            new HealthcareGatewayResolver(
                new IHealthcareTransactionGateway[] { gateway },
                Options.Create(new HealthcareTransactionOptions { DefaultGateway = "Stedi" }),
                NullLogger<HealthcareGatewayResolver>.Instance),
            processor,
            NullLogger<RemittanceIngress>.Instance);

        var result = await ingress.IngestDiscoveredAsync(new ClaimAcknowledgmentDiscovery
        {
            GatewayName = "Stedi",
            ExternalAcknowledgmentId = "era-txn-auth",
            EventId = "evt-auth",
            TransactionSetIdentifier = "835",
            Direction = "INBOUND"
        });

        result.Processed.Should().BeTrue();
        result.ErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
        var stored = await receipts.GetByIdempotencyKeyAsync("Stedi", "era-txn-auth");
        stored!.Status.Should().Be(RemittanceLifecycleStatus.Failed);
        stored.LastErrorCategory.Should().Be(GatewayErrorCategory.Authentication);
        stored.LastError.Should().Be("api-key-rejected");
    }

    private static (RemittanceProcessor Processor,
        InMemoryClaimTransmissionStore Transmissions,
        InMemoryClaimAcknowledgmentStore Acks,
        InMemoryRemittanceStore Receipts) Create()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var receipts = new InMemoryRemittanceStore();
        var processor = new RemittanceProcessor(
            receipts, transmissions, NullLogger<RemittanceProcessor>.Instance);
        return (processor, transmissions, acks, receipts);
    }

    private static async Task<ClaimTransmissionRecord> SeedTransmissionAsync(
        InMemoryClaimTransmissionStore store,
        string claimId = "CLM-P-1001",
        string? pcn = null,
        string? payerCcn = null)
    {
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = claimId,
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            SubmissionId = "synthetic-sub-001",
            PatientControlNumber = pcn ?? claimId,
            PayerClaimControlNumber = payerCcn,
            SubmittedAtUtc = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(tx);
        return tx;
    }

    private static GatewayRemittance PaidRemittance(
        string? payerCcn = "PAYER-CCN-9",
        string? patient = "CLM-P-1001") =>
        new()
        {
            RemittanceId = "era-1",
            Gateway = "Stedi",
            PaymentAmount = 320m,
            PaymentDate = new DateOnly(2026, 1, 20),
            PaymentMethodCode = "ACH",
            ReceivedAt = DateTimeOffset.UtcNow,
            Claims =
            {
                new RemittedClaim
                {
                    PayerClaimControlNumber = payerCcn,
                    PatientControlNumber = patient,
                    ClaimStatusCode = "1",
                    ChargedAmount = 500m,
                    AllowedAmount = 400m,
                    PaidAmount = 320m,
                    PatientResponsibilityAmount = 80m,
                    Adjustments =
                    {
                        new RemittanceAdjustment
                        {
                            GroupCode = "CO", ReasonCode = "45", Amount = 100m,
                            Kind = RemittanceAdjustmentKind.Contractual
                        },
                        new RemittanceAdjustment
                        {
                            GroupCode = "PR", ReasonCode = "1", Amount = 50m,
                            Kind = RemittanceAdjustmentKind.Deductible
                        },
                        new RemittanceAdjustment
                        {
                            GroupCode = "PR", ReasonCode = "2", Amount = 30m,
                            Kind = RemittanceAdjustmentKind.Coinsurance
                        }
                    },
                    ServiceLines =
                    {
                        new RemittedServiceLine
                        {
                            LineIdentifier = "1",
                            LineNumber = 1,
                            ProcedureCode = "90837",
                            ChargedAmount = 500m,
                            PaidAmount = 320m
                        }
                    }
                }
            }
        };

    private sealed class StubRemittanceGateway : IRemittanceGateway
    {
        public string Name => "Stedi";

        public IReadOnlySet<GatewayCapability> Capabilities { get; } =
            new HashSet<GatewayCapability> { GatewayCapability.Remittance };

        public GatewayResponse<GatewayRemittance>? Next { get; init; }

        public Task<GatewayResponse<GatewayRemittance>> RetrieveRemittanceAsync(
            RemittanceRetrievalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Next!);
    }
}
