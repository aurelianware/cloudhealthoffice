using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class RemittancePosterPhiLoggingTests
{
    [Fact]
    public async Task Post_DoesNotLogCheckNumberMemberIdOrTrace()
    {
        const string memberId = "SECRETMEMBER835";
        const string trace = "EFT-TRACE-SECRET";
        var logger = new CapturingLogger<RemittancePoster>();
        var transmissions = new InMemoryClaimTransmissionStore();
        await transmissions.SaveAsync(new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.AcknowledgmentAccepted,
            IdempotencyKey = "k",
            PatientControlNumber = "CLM-P-1001",
            PayerClaimControlNumber = "PAYER-CCN-9",
            SubmittedAtUtc = DateTimeOffset.UtcNow,
            InquirySource = new ClaimStatusInquirySource
            {
                Subscriber = new GatewayEligibilityPerson { MemberId = memberId }
            }
        });
        var receipts = new InMemoryRemittanceStore();
        await new RemittanceProcessor(receipts, transmissions, NullLogger<RemittanceProcessor>.Instance)
            .ProcessAsync(new GatewayRemittance
            {
                RemittanceId = "era-phi",
                Gateway = "Stedi",
                PaymentIdentifier = trace,
                PaymentAmount = 10m,
                ReceivedAt = DateTimeOffset.UtcNow,
                Claims =
                {
                    new RemittedClaim
                    {
                        PayerClaimControlNumber = "PAYER-CCN-9",
                        PaidAmount = 10m
                    }
                }
            });
        var stored = await receipts.GetByIdempotencyKeyAsync("Stedi", "era-phi");
        var poster = new RemittancePoster(
            receipts, transmissions,
            new InMemoryClaimRemittancePostingSink(),
            new InMemoryRemittanceAccumulatorSink(),
            logger);
        await poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        var logs = string.Join("\n", logger.Messages);
        logs.Should().NotBeEmpty();
        logs.Should().NotContain(memberId);
        logs.Should().NotContain(trace);
        logs.Should().Contain("Remittance posted");
    }
}
