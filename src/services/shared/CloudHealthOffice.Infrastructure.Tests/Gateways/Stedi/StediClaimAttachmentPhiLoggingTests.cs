using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimAttachmentPhiLoggingTests
{
    [Fact]
    public async Task SubmitAttachment_DoesNotLogPhiBytesFileNameOrApiKey()
    {
        const string apiKey = "SUPER-SECRET-KEY";
        const string memberName = "Zzyphisurname";
        const string memberId = "SECRETMEMBER123";
        const string unsafeFile = "John_Doe_HIV_results.pdf";
        var payload = Encoding.UTF8.GetBytes("%PDF-1.4 " + memberName + " " + memberId);

        var options = Options.Create(new StediGatewayOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://healthcare.test",
            ClaimsBaseUrl = "https://claims.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            ClaimAttachmentCreatePath = "/2025-03-07/claim-attachments/file",
            MaxRetries = 1
        });

        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created,
                "{\"attachmentId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"uploadUrl\":\"https://s3.amazonaws.com/bucket/key\"}")
            .EnqueueStatus(HttpStatusCode.OK);

        var transmissions = new InMemoryClaimTransmissionStore();
        var attachments = new InMemoryClaimAttachmentTransmissionStore();
        var content = new InMemoryClaimAttachmentContentStore();
        var factory = new StubHttpClientFactory(handler, "https://claims.test");
        var apiLogger = new CapturingLogger<StediClaimAttachmentApiClient>();
        var gatewayLogger = new CapturingLogger<StediHealthcareGateway>();
        var attachmentClient = new StediClaimAttachmentApiClient(
            factory, options, content, apiLogger, delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            new StediEligibilityApiClient(factory, options, NullLogger<StediEligibilityApiClient>.Instance,
                delay: (_, _) => Task.CompletedTask),
            PayerTestHarness.CreateResolver(options),
            options,
            gatewayLogger,
            claimClient: null,
            transmissions: transmissions,
            attachmentClient: attachmentClient,
            attachmentStore: attachments,
            content: content);

        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            PayerId = "60054",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            ServiceLineNumbers = { 1 }
        };
        await transmissions.SaveAsync(tx);
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = tx.TenantId,
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-phi",
            ContentType = "application/pdf",
            DisplayName = unsafeFile
        }, new MemoryStream(payload));

        await gateway.SubmitAttachmentAsync(new ClaimAttachmentSubmissionRequest
        {
            TenantId = tx.TenantId,
            ClaimId = tx.ClaimId,
            TransmissionId = tx.TransmissionId,
            PayerId = tx.PayerId,
            AttachmentId = "att-phi",
            FileName = unsafeFile,
            ContentType = stored.ContentType,
            ContentLength = stored.ContentLength,
            Content = stored
        });

        var logs = string.Join("\n", apiLogger.Messages.Concat(gatewayLogger.Messages));
        logs.Should().NotBeEmpty();
        logs.Should().NotContain(apiKey);
        logs.Should().NotContain(memberName);
        logs.Should().NotContain(memberId);
        logs.Should().NotContain(unsafeFile);
        logs.Should().NotContain("HIV");
        logs.Should().NotContain("%PDF");
        logs.Should().NotContain(Convert.ToBase64String(payload));
        logs.Should().NotContain("s3.amazonaws.com");
        logs.Should().Contain("att-phi");
        logs.Should().Contain("application/pdf");
        logs.Should().Contain("ClaimAttachment275");
    }
}
