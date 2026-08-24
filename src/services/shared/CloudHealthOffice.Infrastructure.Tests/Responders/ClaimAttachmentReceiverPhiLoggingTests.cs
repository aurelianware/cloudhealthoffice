using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class ClaimAttachmentReceiverPhiLoggingTests
{
    [Fact]
    public async Task Receive_DoesNotLogPhiFilenameOrBytes()
    {
        const string unsafeFile = "John_Doe_HIV_results.jpg";
        const string memberId = "SECRETMEMBER123";
        var logger = new CapturingLogger<CloudHealthOfficeClaimAttachmentReceiver>();
        var receiver = new CloudHealthOfficeClaimAttachmentReceiver(
            new PayerEligibilityRouter(new InMemoryPayerEligibilityDirectory()),
            new InMemoryPayerClaimDirectory(),
            new InMemoryClaimAttachmentContentStore(),
            new InMemoryInboundClaimAttachmentReceiptStore(),
            logger);

        await receiver.ReceiveAsync(
            new InboundClaimAttachment
            {
                PayerId = ChoDemoEligibilitySeed.ExternalPayerId,
                ClaimId = ChoDemoClaimAttachmentSeed.ClaimId,
                FileName = unsafeFile,
                ContentType = "image/jpeg",
                AttachmentType = ClaimAttachmentType.DentalImage,
                PatientControlNumber = memberId
            },
            new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]));

        var logs = string.Join("\n", logger.Messages);
        logs.Should().NotBeEmpty();
        logs.Should().NotContain(unsafeFile);
        logs.Should().NotContain("HIV");
        logs.Should().NotContain("PHI-BODY");
        logs.Should().NotContain(memberId);
        logs.Should().NotContain("John_Doe");
        logs.Should().Contain("AvailableToClaim");
    }
}
