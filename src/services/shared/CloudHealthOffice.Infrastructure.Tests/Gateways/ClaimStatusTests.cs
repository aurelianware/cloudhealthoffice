using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimStatusTests
{
    [Fact]
    public async Task RequestFromTransmissionId_DerivesPayerProviderSubscriberAndDates()
    {
        var (gateway, transmissions, _, _) = await SeedAsync(GatewayClaimFixtures.Professional());

        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();
        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            CorrelationId = "corr-status-1"
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.ClaimId.Should().Be("CLM-P-1001");
        response.Result.TransmissionId.Should().Be(tx.TransmissionId);
        response.Result.PatientControlNumber.Should().Be(tx.PatientControlNumber);
        response.Result.Status.Should().Be(GatewayClaimStatus.InProcess);
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.ClaimStatus276277);
        response.Metadata.GatewayName.Should().Be("Mock");
        response.Metadata.CorrelationId.Should().Be("corr-status-1");
        tx.InquirySource!.BillingProvider!.Npi.Should().Be("1999999984");
        tx.InquirySource.Subscriber!.MemberId.Should().Be("U7777788888");
    }

    [Fact]
    public async Task RequestFromClaimId_SelectsLatestTransmission()
    {
        var (gateway, _, _, _) = await SeedAsync(GatewayClaimFixtures.Professional());

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001"
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.ClaimId.Should().Be("CLM-P-1001");
        response.Result.Status.Should().Be(GatewayClaimStatus.InProcess);
    }

    [Fact]
    public async Task UsesPayerClaimControlNumberFrom277caWhenAvailable()
    {
        var (gateway, transmissions, acks, _) = await SeedAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();
        var processor = new ClaimAcknowledgmentProcessor(
            acks, transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-1",
            Gateway = "Mock",
            TransmissionId = tx.TransmissionId,
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Accepted,
            ClaimControlNumber = "PAYER-CCN-001",
            PatientControlNumber = tx.PatientControlNumber,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        var stored = await transmissions.GetByIdAsync(tx.TransmissionId);
        stored!.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        stored.PayerClaimControlNumber.Should().Be("PAYER-CCN-001");

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.PayerClaimControlNumber.Should().Be("PAYER-CCN-001");
        (await transmissions.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Single().Status
            .Should().Be(ClaimAcknowledgmentStatus.Accepted);
    }

    [Fact]
    public async Task FallsBackToPatientControlNumberWhenPayerControlNumberMissing()
    {
        var (gateway, transmissions, _, _) = await SeedAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.Result!.PayerClaimControlNumber.Should().BeNull();
        response.Result.PatientControlNumber.Should().Be(tx.PatientControlNumber);
    }

    [Fact]
    public async Task InvalidTransmission_FailsTransmissionNotFound()
    {
        var gateway = new MockHealthcareGateway(NullLogger<MockHealthcareGateway>.Instance);
        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = "does-not-exist"
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.TransmissionNotFound);
    }

    [Fact]
    public async Task InvalidClaimId_FailsClaimNotFound()
    {
        var gateway = new MockHealthcareGateway(NullLogger<MockHealthcareGateway>.Instance);
        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-MISSING"
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ClaimNotFound);
    }

    [Fact]
    public async Task CrossTenantClaimId_DoesNotSeeOtherTenant()
    {
        var (gateway, _, _, _) = await SeedAsync(GatewayClaimFixtures.Professional());
        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-beta",
            ClaimId = "CLM-P-1001"
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ClaimNotFound);
    }

    [Fact]
    public async Task ServiceLineInquiry_PreservesLineStatus()
    {
        var (gateway, transmissions, _, _) = await SeedAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            ServiceLineNumber = 1
        });

        response.IsSuccess.Should().BeTrue();
        response.Result!.ServiceLineStatuses.Should().ContainSingle(l => l.LineNumber == 1);
        response.Result.ServiceLineStatuses[0].ProcedureCode.Should().Be("90837");
    }

    [Fact]
    public async Task InvalidServiceLine_DoesNotFallBackToClaimLevel()
    {
        var (gateway, transmissions, _, _) = await SeedAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            ServiceLineNumber = 99
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceLineNotFound);
    }

    [Fact]
    public async Task ServiceLineNumberWithoutLineDetails_DoesNotWidenToClaimLevel()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance, transmissions: transmissions);
        var source = GatewayClaimFixtures.Professional();
        source.ServiceLines.Clear();
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = MockHealthcareGateway.GatewayName,
            PayerId = "60054",
            PatientControlNumber = "CLM-P-1001",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            ServiceLineNumbers = { 1 },
            InquirySource = ClaimStatusInquirySource.FromSubmission(source),
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
        };
        await transmissions.SaveAsync(tx);

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            ServiceLineNumber = 1
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceLineNotFound);
        response.ErrorMessage.Should().Contain("line details");
    }

    [Fact]
    public async Task PaidClaimStatus_DoesNotChangeAcknowledgmentOrTransmission()
    {
        var request = GatewayClaimFixtures.Professional(claimId: "CLM-PAID-1");
        var (gateway, transmissions, acks, inquiries) = await SeedAsync(request);
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-PAID-1")).Single();
        var processor = new ClaimAcknowledgmentProcessor(
            acks, transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-paid",
            Gateway = "Mock",
            TransmissionId = tx.TransmissionId,
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Accepted,
            ClaimControlNumber = "CCN-PAID",
            ReceivedAt = DateTimeOffset.UtcNow
        });

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.Result!.Status.Should().Be(GatewayClaimStatus.Paid);
        (await transmissions.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Single().Status
            .Should().Be(ClaimAcknowledgmentStatus.Accepted);
        (await inquiries.ListByTransmissionIdAsync(tx.TransmissionId)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeniedClaimStatus_IsNotInterpretedAs277caRejection()
    {
        var request = GatewayClaimFixtures.Professional(claimId: "CLM-DENIED-1");
        var (gateway, transmissions, acks, _) = await SeedAsync(request);
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-DENIED-1")).Single();
        var processor = new ClaimAcknowledgmentProcessor(
            acks, transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance);
        await processor.ProcessAsync(new GatewayClaimAcknowledgment
        {
            AcknowledgmentId = "ack-den",
            Gateway = "Mock",
            TransmissionId = tx.TransmissionId,
            OriginalSubmissionId = tx.SubmissionId,
            Status = ClaimAcknowledgmentStatus.Accepted,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.Result!.Status.Should().Be(GatewayClaimStatus.Denied);
        (await acks.ListByTransmissionIdAsync(tx.TransmissionId)).Single().Status
            .Should().Be(ClaimAcknowledgmentStatus.Accepted);
        (await transmissions.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
    }

    [Fact]
    public async Task NoRecordFound_IsBusinessOutcomeNotTransportFailure()
    {
        var request = GatewayClaimFixtures.Professional(claimId: "CLM-NORECORD-1");
        var (gateway, transmissions, _, _) = await SeedAsync(request);
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-NORECORD-1")).Single();

        var response = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        response.IsSuccess.Should().BeTrue();
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
        response.Result!.Status.Should().Be(GatewayClaimStatus.NoRecordFound);
        response.Result.MatchCount.Should().Be(0);
    }

    [Fact]
    public async Task ReplaySameExternalTransaction_DoesNotDuplicateSnapshot()
    {
        var (gateway, transmissions, _, inquiries) = await SeedAsync(GatewayClaimFixtures.Professional());
        var tx = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-P-1001")).Single();
        var first = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });
        var second = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId
        });

        second.Result!.ReplayOfExistingInquiry.Should().BeTrue();
        second.Result.InquiryId.Should().Be(first.Result!.InquiryId);
        (await inquiries.ListByTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task HistoryIsChronologicalAndTenantIsolated()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var inquiries = new InMemoryClaimStatusInquiryStore();
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance, transmissions: transmissions, statusInquiries: inquiries);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional(claimId: "CLM-PEND-1"));
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional(claimId: "CLM-FINAL-1"));
        var pending = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-PEND-1")).Single();
        var finalized = (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-FINAL-1")).Single();

        await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = pending.TransmissionId
        });
        await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = finalized.TransmissionId
        });

        var pendingHistory = await inquiries.ListByTransmissionIdAsync(pending.TransmissionId);
        pendingHistory.Should().ContainSingle();
        pendingHistory[0].NormalizedStatus.Should().Be(GatewayClaimStatus.Pending);
        pendingHistory[0].TenantId.Should().Be("tenant-alpha");

        (await inquiries.ListByTenantAndClaimIdAsync("tenant-beta", "CLM-PEND-1")).Should().BeEmpty();
    }

    [Fact]
    public void FollowUpSeam_ExcludesTerminalAndRejectedAcknowledgments()
    {
        var accepted = new ClaimTransmissionRecord
        {
            Status = GatewayClaimTransmissionStatus.AcknowledgmentAccepted
        };
        var rejected = new ClaimTransmissionRecord
        {
            Status = GatewayClaimTransmissionStatus.AcknowledgmentRejected
        };
        var inProcess = new ClaimStatusInquiryRecord { NormalizedStatus = GatewayClaimStatus.InProcess };
        var paid = new ClaimStatusInquiryRecord { NormalizedStatus = GatewayClaimStatus.Paid };

        ClaimStatusRules.IsFollowUpCandidate(accepted, inProcess).Should().BeTrue();
        ClaimStatusRules.IsFollowUpCandidate(accepted, paid).Should().BeFalse();
        ClaimStatusRules.IsFollowUpCandidate(rejected, inProcess).Should().BeFalse();
    }

    [Fact]
    public async Task InstitutionalAndDental_DeriveFromOriginalTransmission()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance, transmissions: transmissions);
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Institutional());
        await gateway.SubmitClaimAsync(GatewayClaimFixtures.Dental());

        var inst = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-I-2001"
        });
        var dental = await gateway.CheckClaimStatusAsync(new ClaimStatusRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-D-3001"
        });

        inst.IsSuccess.Should().BeTrue();
        dental.IsSuccess.Should().BeTrue();
        (await transmissions.FindByTenantAndClaimIdAsync("tenant-alpha", "CLM-I-2001"))
            .Single().InquirySource!.TypeOfBill.Should().Be("111");
    }

    private static async Task<(
        MockHealthcareGateway Gateway,
        InMemoryClaimTransmissionStore Transmissions,
        InMemoryClaimAcknowledgmentStore Acks,
        InMemoryClaimStatusInquiryStore Inquiries)> SeedAsync(GatewayClaimSubmissionRequest request)
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var inquiries = new InMemoryClaimStatusInquiryStore();
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance,
            transmissions: transmissions,
            acknowledgments: acks,
            statusInquiries: inquiries);
        var submitted = await gateway.SubmitClaimAsync(request);
        submitted.IsSuccess.Should().BeTrue();
        return (gateway, transmissions, acks, inquiries);
    }
}
