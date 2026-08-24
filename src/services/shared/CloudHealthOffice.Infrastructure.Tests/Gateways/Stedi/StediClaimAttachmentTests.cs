using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

public class StediClaimAttachmentTests
{
    private const string CreateJson =
        "{\"attachmentId\":\"d3b3e3e3-3e3e-3e3e-3e3e-3e3e3e3e3e3e\",\"uploadUrl\":\"https://s3.amazonaws.com/bucket/key\"}";

    private static readonly byte[] PdfBytes = "%PDF-1.4 synthetic"u8.ToArray();

    private static StediGatewayOptions ValidOptions() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://healthcare.test",
        ClaimsBaseUrl = "https://claims.test",
        Environment = "sandbox",
        EligibilityPath = "/eligibility/v3",
        ClaimAttachmentCreatePath = "/2025-03-07/claim-attachments/file",
        MaxRetries = 1
    };

    private static (StediHealthcareGateway Gateway,
        StubHttpMessageHandler Handler,
        InMemoryClaimTransmissionStore Transmissions,
        InMemoryClaimAttachmentTransmissionStore Attachments,
        InMemoryClaimAttachmentContentStore Content)
        NewGateway(
            StubHttpMessageHandler? handler = null,
            InMemoryClaimTransmissionStore? transmissions = null,
            InMemoryClaimAttachmentTransmissionStore? attachments = null,
            InMemoryClaimAttachmentContentStore? content = null)
    {
        handler ??= new StubHttpMessageHandler();
        var opts = Options.Create(ValidOptions());
        var factory = new StubHttpClientFactory(handler, "https://claims.test");
        transmissions ??= new InMemoryClaimTransmissionStore();
        attachments ??= new InMemoryClaimAttachmentTransmissionStore();
        content ??= new InMemoryClaimAttachmentContentStore();
        var eligibility = new StediEligibilityApiClient(
            factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var attachmentClient = new StediClaimAttachmentApiClient(
            factory, opts, content, NullLogger<StediClaimAttachmentApiClient>.Instance,
            delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            eligibility,
            PayerTestHarness.CreateResolver(opts),
            opts,
            NullLogger<StediHealthcareGateway>.Instance,
            claimClient: null,
            transmissions: transmissions,
            attachmentClient: attachmentClient,
            attachmentStore: attachments,
            content: content);
        return (gateway, handler, transmissions, attachments, content);
    }

    private static async Task<(ClaimTransmissionRecord Tx, ClaimAttachmentContentReference Stored)> SeedAsync(
        InMemoryClaimTransmissionStore transmissions,
        InMemoryClaimAttachmentContentStore content,
        GatewayClaimType type = GatewayClaimType.Professional,
        string claimId = "CLM-P-1001",
        string payerId = "60054",
        int[]? lines = null)
    {
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = claimId,
            GatewayName = "Stedi",
            ClaimType = type,
            PayerId = payerId,
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            ServiceLineNumbers = (lines ?? new[] { 1 }).ToList()
        };
        await transmissions.SaveAsync(tx);
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = tx.TenantId,
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-1",
            ContentType = "application/pdf",
            DisplayName = "clinical-note.pdf"
        }, new MemoryStream(PdfBytes));
        return (tx, stored);
    }

    private static ClaimAttachmentSubmissionRequest Request(
        ClaimTransmissionRecord tx,
        ClaimAttachmentContentReference stored,
        int? serviceLine = null,
        ClaimAttachmentType type = ClaimAttachmentType.ClinicalNote) =>
        new()
        {
            TenantId = tx.TenantId,
            ClaimId = tx.ClaimId,
            TransmissionId = tx.TransmissionId,
            PayerId = tx.PayerId,
            AttachmentId = "att-1",
            AttachmentType = type,
            ContentType = stored.ContentType,
            ContentLength = stored.ContentLength,
            Content = stored,
            ServiceLineNumber = serviceLine
        };

    [Fact]
    public async Task ApiClient_CreateAndPut_UsesDocumentedJsonContract()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var opts = Options.Create(ValidOptions());
        var content = new InMemoryClaimAttachmentContentStore();
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = "tx1",
            AttachmentId = "att-1",
            ContentType = "application/pdf"
        }, new MemoryStream(PdfBytes));
        var client = new StediClaimAttachmentApiClient(
            new StubHttpClientFactory(handler, "https://claims.test"),
            opts,
            content,
            NullLogger<StediClaimAttachmentApiClient>.Instance,
            delay: (_, _) => Task.CompletedTask);

        var result = await client.SubmitAsync(new ClaimAttachmentSubmissionRequest
        {
            ContentType = "application/pdf",
            Content = stored
        }, stored, CancellationToken.None);

        result.Response.AttachmentId.Should().Be("d3b3e3e3-3e3e-3e3e-3e3e-3e3e3e3e3e3e");
        handler.CallCount.Should().Be(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[1].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public void Mapper_CreateFileRequest_UsesDocumentedContentType()
    {
        var dto = StediClaimAttachmentMapper.ToCreateFileRequest(new ClaimAttachmentSubmissionRequest
        {
            ContentType = "image/jpg"
        });
        var json = JsonSerializer.Serialize(dto, StediHttpSender.JsonOptions);
        json.Should().Contain("\"contentType\":\"image/jpeg\"");
        json.Should().NotContain("base64");
        json.Should().NotContain("x12");
    }

    [Theory]
    [InlineData(ClaimAttachmentType.MedicalRecord, "M1")]
    [InlineData(ClaimAttachmentType.OperativeReport, "OB")]
    [InlineData(ClaimAttachmentType.LabResult, "LA")]
    [InlineData(ClaimAttachmentType.Radiograph, "RB")]
    [InlineData(ClaimAttachmentType.PeriodontalChart, "P6")]
    [InlineData(ClaimAttachmentType.IntraoralImage, "XP")]
    [InlineData(ClaimAttachmentType.DentalNarrative, "OZ")]
    public void Mapper_AttachmentType_UsesPwk01Codes(ClaimAttachmentType type, string code)
    {
        StediClaimAttachmentMapper.ToAttachmentReportTypeCode(type).Should().Be(code);
        StediClaimAttachmentMapper.AttachmentTransmissionCode.Should().Be("EL");
    }

    [Theory]
    [InlineData(GatewayClaimType.Professional, "CLM-P-1001")]
    [InlineData(GatewayClaimType.Institutional, "CLM-I-2001")]
    [InlineData(GatewayClaimType.Dental, "CLM-D-3001")]
    public async Task ClaimTypes_ClaimLevel_Succeed(GatewayClaimType type, string claimId)
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content, type, claimId);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored,
            type: type == GatewayClaimType.Dental ? ClaimAttachmentType.Radiograph : ClaimAttachmentType.ClinicalNote));

        response.IsSuccess.Should().BeTrue($"{response.ErrorMessage} {response.Metadata.ErrorCategory}");
        response.Result!.AssociationLevel.Should().Be(ClaimAttachmentAssociationLevel.Claim);
        response.Result.ExternalTransactionId.Should().Be("d3b3e3e3-3e3e-3e3e-3e3e-3e3e3e3e3e3e");
    }

    [Fact]
    public async Task ProfessionalServiceLine_Succeeds()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored, serviceLine: 1));

        response.IsSuccess.Should().BeTrue();
        response.Result!.ServiceLineNumber.Should().Be(1);
    }

    [Fact]
    public async Task Success_DoesNotSendApiKeyOnUploadPut()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        await gateway.SubmitAttachmentAsync(Request(tx, stored));

        handler.CallCount.Should().Be(2);
        handler.Requests[0].RequestUri!.ToString().Should().Contain("/2025-03-07/claim-attachments/file");
        handler.RequestBodies[0].Should().Contain("\"contentType\":\"application/pdf\"");
        StubHttpClientFactory.Auth(handler.Requests[0]).Should().NotBeNull();
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[1].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotCallStediAgain()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var first = await gateway.SubmitAttachmentAsync(Request(tx, stored));
        var second = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        handler.CallCount.Should().Be(2);
        second.Result!.ReplayOfExistingTransmission.Should().BeTrue();
        second.Result.AttachmentTransmissionId.Should().Be(first.Result!.AttachmentTransmissionId);
    }

    [Fact]
    public async Task EnrollmentRequired_IsSurfaced()
    {
        var handler = new StubHttpMessageHandler();
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content, payerId: SyntheticPayerSeed.EnrollmentId);
        var request = Request(tx, stored);

        var response = await gateway.SubmitAttachmentAsync(request);

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.EnrollmentRequired);
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PayerUnsupported_IsSurfaced()
    {
        var handler = new StubHttpMessageHandler();
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            PayerId = SyntheticPayerSeed.UnsupportedId,
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            ServiceLineNumbers = { 1 }
        };
        await transmissions.SaveAsync(tx);
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = tx.TenantId,
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-1",
            ContentType = "application/pdf"
        }, new MemoryStream(PdfBytes));

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.NotSupported);
        handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, GatewayErrorCategory.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, GatewayErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, GatewayErrorCategory.Authorization)]
    [InlineData(HttpStatusCode.NotFound, GatewayErrorCategory.Validation)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, GatewayErrorCategory.AttachmentTooLarge)]
    public async Task HttpClientErrors_AreNotRetried(HttpStatusCode status, GatewayErrorCategory category)
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(status);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(category);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Http429_IsRetriedThenRateLimited()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests)
            .EnqueueStatus(HttpStatusCode.TooManyRequests);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.RateLimited);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task Http5xx_IsRetried()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Timeout_IsTransient()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueThrow(new TaskCanceledException("timeout"))
            .EnqueueThrow(new TaskCanceledException("timeout"));
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Timeout);
    }

    [Fact]
    public async Task NetworkError_IsConnectivity()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueThrow(new HttpRequestException("dns"))
            .EnqueueThrow(new HttpRequestException("dns"));
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Connectivity);
    }

    [Fact]
    public async Task MalformedCreateResponse_IsMalformed()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.Created, "{not-json");
        var (gateway, _, transmissions, _, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.MalformedResponse);
    }

    [Fact]
    public async Task TransientFailureRetry_DoesNotDuplicateAcceptedRecord()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.InternalServerError)
            .EnqueueStatus(HttpStatusCode.InternalServerError);
        var (gateway, _, transmissions, attachments, content) = NewGateway(handler);
        var (tx, stored) = await SeedAsync(transmissions, content);

        var first = await gateway.SubmitAttachmentAsync(Request(tx, stored));
        first.IsSuccess.Should().BeFalse();

        var retryHandler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.Created, CreateJson)
            .EnqueueStatus(HttpStatusCode.OK);
        var retryGateway = NewGateway(retryHandler, transmissions, attachments, content).Gateway;
        var second = await retryGateway.SubmitAttachmentAsync(Request(tx, stored));
        second.IsSuccess.Should().BeTrue();
        (await attachments.ListByClaimTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(1);
    }
}
