using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging.Abstractions;

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
}
