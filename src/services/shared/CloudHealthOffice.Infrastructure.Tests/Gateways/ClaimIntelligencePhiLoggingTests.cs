using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimIntelligencePhiLoggingTests
{
    [Fact]
    public async Task Compose_DoesNotLogPatientNameMemberIdOrDob()
    {
        const string memberId = "SECRETMEMBER999";
        const string lastName = "Zzyphisurname";
        const string firstName = "PhiFirst";
        var logger = new CapturingLogger<ClaimIntelligenceComposer>();
        var transmissions = new InMemoryClaimTransmissionStore();
        await transmissions.SaveAsync(new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-PHI-1",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = "k",
            PatientControlNumber = "CLM-PHI-1",
            SubmittedAtUtc = DateTimeOffset.UtcNow,
            InquirySource = new ClaimStatusInquirySource
            {
                Subscriber = new GatewayEligibilityPerson
                {
                    MemberId = memberId,
                    FirstName = firstName,
                    LastName = lastName,
                    DateOfBirth = new DateOnly(1980, 5, 1)
                }
            }
        });

        var composer = new ClaimIntelligenceComposer(
            transmissions,
            new InMemoryClaimAcknowledgmentStore(),
            new InMemoryClaimStatusInquiryStore(),
            new InMemoryClaimAttachmentTransmissionStore(),
            new InMemoryInboundClaimAttachmentReceiptStore(),
            new InMemoryRemittanceStore(),
            logger);

        var view = await composer.ComposeAsync(new ClaimIntelligenceRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-PHI-1"
        });

        view.Should().NotBeNull();
        view!.Patient!.MemberId.Should().Be(memberId);
        var logs = string.Join("\n", logger.Messages);
        logs.Should().NotBeEmpty();
        logs.Should().NotContain(memberId);
        logs.Should().NotContain(lastName);
        logs.Should().NotContain(firstName);
        logs.Should().NotContain("1980-05-01");
        logs.Should().Contain("Claim intelligence composed");
        logs.Should().Contain("AcceptedByClearinghouse");
    }
}
