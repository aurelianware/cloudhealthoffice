using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimAttachmentPhiLoggingTests
{
    [Fact]
    public async Task MockSubmitAttachment_DoesNotLogFileContentsOrUnsafeName()
    {
        const string unsafeFile = "John_Doe_HIV_results.pdf";
        var logger = new CapturingLogger<MockHealthcareGateway>();
        var transmissions = new CloudHealthOffice.Infrastructure.Gateways.InMemoryClaimTransmissionStore();
        var content = new CloudHealthOffice.Infrastructure.Gateways.InMemoryClaimAttachmentContentStore();
        var gateway = new MockHealthcareGateway(logger, transmissions: transmissions, content: content);

        var submitted = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = submitted.Result!.TransmissionId,
            AttachmentId = "att-1",
            ContentType = "application/pdf",
            DisplayName = unsafeFile
        }, new MemoryStream("%PDF-1.4 PHI-BODY"u8.ToArray()));

        await gateway.SubmitAttachmentAsync(new ClaimAttachmentSubmissionRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            TransmissionId = submitted.Result.TransmissionId,
            PayerId = "60054",
            AttachmentId = "att-1",
            FileName = unsafeFile,
            ContentType = stored.ContentType,
            ContentLength = stored.ContentLength,
            Content = stored
        });

        var logs = string.Join("\n", logger.Messages);
        logs.Should().NotBeEmpty();
        logs.Should().NotContain(unsafeFile);
        logs.Should().NotContain("HIV");
        logs.Should().NotContain("PHI-BODY");
        logs.Should().NotContain("%PDF");
        logs.Should().Contain("att-1");
        logs.Should().Contain("ClaimAttachment275");
    }
}
