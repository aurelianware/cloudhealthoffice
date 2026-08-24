using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class ClaimAttachmentTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.4 synthetic"u8.ToArray();
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    private static (MockHealthcareGateway Gateway,
        InMemoryClaimTransmissionStore Transmissions,
        InMemoryClaimAttachmentTransmissionStore Attachments,
        InMemoryClaimAttachmentContentStore Content)
        NewHarness(ClaimAttachmentOptions? attachmentOptions = null, IMessageBus? bus = null)
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var attachments = new InMemoryClaimAttachmentTransmissionStore();
        var options = Options.Create(new HealthcareTransactionOptions
        {
            ClaimAttachments = attachmentOptions ?? new ClaimAttachmentOptions()
        });
        var content = new InMemoryClaimAttachmentContentStore(options.Value.ClaimAttachments);
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance,
            transmissions: transmissions,
            attachments: attachments,
            content: content,
            options: options,
            messageBus: bus);
        return (gateway, transmissions, attachments, content);
    }

    private static async Task<(ClaimTransmissionRecord Transmission, ClaimAttachmentContentReference Content)> SeedAsync(
        MockHealthcareGateway gateway,
        InMemoryClaimAttachmentContentStore content,
        GatewayClaimSubmissionRequest? claim = null,
        byte[]? bytes = null,
        string contentType = "application/pdf",
        string attachmentId = "att-1",
        string? fileName = "note.pdf")
    {
        claim ??= GatewayClaimFixtures.Professional();
        var submitted = await gateway.SubmitClaimAsync(claim);
        submitted.IsSuccess.Should().BeTrue();
        var transmissionId = submitted.Result!.TransmissionId;
        bytes ??= PdfBytes;
        await using var stream = new MemoryStream(bytes);
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = claim.TenantId,
            TransmissionId = transmissionId,
            AttachmentId = attachmentId,
            ContentType = contentType,
            DisplayName = fileName
        }, stream);
        return (new ClaimTransmissionRecord { TransmissionId = transmissionId, TenantId = claim.TenantId, ClaimId = claim.ClaimId }, stored);
    }

    private static ClaimAttachmentSubmissionRequest Request(
        ClaimTransmissionRecord tx,
        ClaimAttachmentContentReference content,
        string attachmentId = "att-1",
        int? serviceLine = null,
        ClaimAttachmentType type = ClaimAttachmentType.ClinicalNote,
        string? tenantId = null,
        string? claimId = null,
        string? payerId = "60054") =>
        new()
        {
            TenantId = tenantId ?? tx.TenantId,
            ClaimId = claimId ?? "CLM-P-1001",
            TransmissionId = tx.TransmissionId,
            PayerId = payerId,
            AttachmentId = attachmentId,
            AttachmentType = type,
            FileName = content.DisplayName,
            ContentType = content.ContentType,
            ContentLength = content.ContentLength,
            Content = content,
            ServiceLineNumber = serviceLine,
            CreatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task ValidClaimTransmission_IsAccepted()
    {
        var (gateway, _, attachments, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        response.IsSuccess.Should().BeTrue();
        response.Result!.AcceptedForProcessing.Should().BeTrue();
        response.Result.Status.Should().Be(ClaimAttachmentTransmissionStatus.GatewayAccepted);
        response.Result.AssociationLevel.Should().Be(ClaimAttachmentAssociationLevel.Claim);
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.ClaimAttachment275);
        var persisted = await attachments.GetByIdAsync(response.Result.AttachmentTransmissionId);
        persisted.Should().NotBeNull();
        persisted!.ChecksumSha256.Should().Be(stored.ChecksumSha256);
        persisted.ContentStorageKey.Should().NotContain("note.pdf");
    }

    [Fact]
    public async Task UnknownTransmission_Fails()
    {
        var (gateway, _, _, content) = NewHarness();
        await using var stream = new MemoryStream(PdfBytes);
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = "missing",
            AttachmentId = "att-1",
            ContentType = "application/pdf"
        }, stream);

        var response = await gateway.SubmitAttachmentAsync(new ClaimAttachmentSubmissionRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            TransmissionId = "does-not-exist",
            AttachmentId = "att-1",
            ContentType = "application/pdf",
            ContentLength = stored.ContentLength,
            Content = stored
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.TransmissionNotFound);
    }

    [Fact]
    public async Task ClaimMismatch_Fails()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored, claimId: "OTHER-CLAIM"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ClaimMismatch);
    }

    [Fact]
    public async Task TenantMismatch_Fails()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored, tenantId: "tenant-beta"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ClaimMismatch);
    }

    [Fact]
    public async Task PayerMismatch_Fails()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored, payerId: "99999"));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ClaimMismatch);
    }

    [Fact]
    public async Task ServiceLineMatch_IsAccepted()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored, serviceLine: 1));

        response.IsSuccess.Should().BeTrue();
        response.Result!.AssociationLevel.Should().Be(ClaimAttachmentAssociationLevel.ServiceLine);
        response.Result.ServiceLineNumber.Should().Be(1);
    }

    [Fact]
    public async Task InvalidServiceLine_DoesNotFallBackToClaim()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored, serviceLine: 99));

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceLineNotFound);
    }

    [Fact]
    public async Task DentalServiceLine_IsRejected()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content, GatewayClaimFixtures.Dental(), attachmentId: "att-d");

        var response = await gateway.SubmitAttachmentAsync(new ClaimAttachmentSubmissionRequest
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-D-3001",
            TransmissionId = tx.TransmissionId,
            PayerId = "60054",
            AttachmentId = "att-d",
            AttachmentType = ClaimAttachmentType.Radiograph,
            ContentType = stored.ContentType,
            ContentLength = stored.ContentLength,
            Content = stored,
            ServiceLineNumber = 1
        });

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.NotSupported);
    }

    [Fact]
    public async Task ValidPdfAndJpeg_AreAccepted()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, pdf) = await SeedAsync(gateway, content);
        var jpeg = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-img",
            ContentType = "image/jpeg",
            DisplayName = "xray.jpg"
        }, new MemoryStream(JpegBytes));

        (await gateway.SubmitAttachmentAsync(Request(tx, pdf))).IsSuccess.Should().BeTrue();
        (await gateway.SubmitAttachmentAsync(Request(tx, jpeg, attachmentId: "att-img"))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UnsupportedMime_IsRejected()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);
        stored.ContentType = "application/zip";

        var request = Request(tx, stored);
        request.ContentType = "application/zip";
        var response = await gateway.SubmitAttachmentAsync(request);

        response.IsSuccess.Should().BeFalse();
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.UnsupportedContentType);
    }

    [Fact]
    public async Task OversizedAndZeroByte_AreRejected()
    {
        var options = new ClaimAttachmentOptions { MaxContentLengthBytes = 8, StediMaxContentLengthBytes = 8 };
        var (gateway, _, _, content) = NewHarness(options);
        var submitted = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var transmissionId = submitted.Result!.TransmissionId;

        var actLarge = async () => await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = transmissionId,
            AttachmentId = "big",
            ContentType = "application/pdf"
        }, new MemoryStream(PdfBytes));
        await actLarge.Should().ThrowAsync<InvalidOperationException>();

        var actEmpty = async () => await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = transmissionId,
            AttachmentId = "empty",
            ContentType = "application/pdf"
        }, new MemoryStream());
        await actEmpty.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Checksum_IsSha256_AndChangedContentIsNewVersion()
    {
        var (gateway, _, attachments, content) = NewHarness();
        var (tx, first) = await SeedAsync(gateway, content);
        Convert.ToHexString(SHA256.HashData(PdfBytes)).ToLowerInvariant().Should().Be(first.ChecksumSha256);

        var firstResult = await gateway.SubmitAttachmentAsync(Request(tx, first));
        firstResult.IsSuccess.Should().BeTrue();

        var changed = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-1",
            ContentType = "application/pdf"
        }, new MemoryStream("%PDF-1.4 changed"u8.ToArray()));
        changed.ChecksumSha256.Should().NotBe(first.ChecksumSha256);

        var mutate = await gateway.SubmitAttachmentAsync(Request(tx, changed));
        mutate.IsSuccess.Should().BeFalse();

        var versioned = Request(tx, changed);
        versioned.AttachmentVersion = 2;
        var second = await gateway.SubmitAttachmentAsync(versioned);
        second.IsSuccess.Should().BeTrue();
        second.Result!.AttachmentTransmissionId.Should().NotBe(firstResult.Result!.AttachmentTransmissionId);
        (await attachments.ListByClaimTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(2);
    }

    [Fact]
    public void FileName_IsSanitized_AndNotUsedAsStorageKey()
    {
        var name = ClaimAttachmentRules.SanitizeFileName("../../etc/passwd");
        name.Should().Be("passwd");
        var unsafeName = ClaimAttachmentRules.SanitizeFileName("John_Doe_HIV_results.pdf");
        unsafeName.Should().Be("John_Doe_HIV_results.pdf");
        var key = ClaimAttachmentRules.StorageKey("tenant-alpha", "tx1", "att1", "abc123", "application/pdf");
        key.Should().NotContain("John");
        key.Should().NotContain("HIV");
        key.Should().StartWith("tenant-alpha/tx1/att1/");
    }

    [Fact]
    public async Task Lifecycle_DoesNotChange837Or277CA()
    {
        var (gateway, transmissions, attachments, content) = NewHarness();
        var submitted = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var txId = submitted.Result!.TransmissionId;
        var before = await transmissions.GetByIdAsync(txId);
        before!.Status.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);

        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = txId,
            AttachmentId = "att-1",
            ContentType = "application/pdf"
        }, new MemoryStream(PdfBytes));
        var response = await gateway.SubmitAttachmentAsync(Request(before, stored));
        response.Result!.Status.Should().Be(ClaimAttachmentTransmissionStatus.GatewayAccepted);

        var after = await transmissions.GetByIdAsync(txId);
        after!.Status.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
        after.AcknowledgedAtUtc.Should().BeNull();
        (await attachments.GetByIdAsync(response.Result.AttachmentTransmissionId))!.Status
            .Should().Be(ClaimAttachmentTransmissionStatus.GatewayAccepted);
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotCreateSecondRecord()
    {
        var (gateway, _, attachments, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);

        var first = await gateway.SubmitAttachmentAsync(Request(tx, stored));
        var second = await gateway.SubmitAttachmentAsync(Request(tx, stored));

        second.Result!.ReplayOfExistingTransmission.Should().BeTrue();
        second.Result.AttachmentTransmissionId.Should().Be(first.Result!.AttachmentTransmissionId);
        (await attachments.ListByClaimTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task DifferentAttachmentId_SendsSeparately()
    {
        var (gateway, _, attachments, content) = NewHarness();
        var (tx, a) = await SeedAsync(gateway, content, attachmentId: "att-a");
        var b = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-b",
            ContentType = "application/pdf"
        }, new MemoryStream(PdfBytes));

        await gateway.SubmitAttachmentAsync(Request(tx, a, "att-a"));
        await gateway.SubmitAttachmentAsync(Request(tx, b, "att-b"));
        (await attachments.ListByClaimTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task SameContentDifferentServiceLine_SendsSeparately()
    {
        var (gateway, _, attachments, content) = NewHarness();
        var claim = GatewayClaimFixtures.Professional();
        claim.ServiceLines.Add(new GatewayClaimLine { LineNumber = 2, ProcedureCode = "90834", Units = 1, ChargeAmount = 0m });
        var (tx, stored) = await SeedAsync(gateway, content, claim);

        await gateway.SubmitAttachmentAsync(Request(tx, stored, serviceLine: 1));
        var line2 = Request(tx, stored, serviceLine: 2);
        line2.AttachmentId = "att-line-2";
        var stored2 = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = tx.TransmissionId,
            AttachmentId = "att-line-2",
            ContentType = "application/pdf"
        }, new MemoryStream(PdfBytes));
        line2.Content = stored2;
        await gateway.SubmitAttachmentAsync(line2);
        (await attachments.ListByClaimTransmissionIdAsync(tx.TransmissionId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Persistence_TenantIsolationAndChecksumLookup()
    {
        var store = new InMemoryClaimAttachmentTransmissionStore();
        var record = new ClaimAttachmentTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            ClaimTransmissionId = "tx-1",
            AttachmentId = "att-1",
            IdempotencyKey = "key-1",
            ChecksumSha256 = "abc"
        };
        var (created, _) = await store.TryCreateAsync(record);
        created.Should().BeTrue();
        (await store.TryCreateAsync(record)).Created.Should().BeFalse();
        (await store.FindByChecksumAsync("tenant-alpha", "abc")).Should().ContainSingle();
        (await store.FindByChecksumAsync("tenant-beta", "abc")).Should().BeEmpty();
    }

    [Fact]
    public async Task UnsafeScan_IsRejected()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);
        stored.ScanStatus = ClaimAttachmentScanStatus.Quarantined;
        var response = await gateway.SubmitAttachmentAsync(Request(tx, stored));
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.AttachmentUnsafe);
    }

    [Fact]
    public async Task Solicited_IsUnsupported()
    {
        var (gateway, _, _, content) = NewHarness();
        var (tx, stored) = await SeedAsync(gateway, content);
        var request = Request(tx, stored);
        request.Mode = ClaimAttachmentMode.Solicited;
        request.PayerRequestControlNumber = "rfa-1";
        var response = await gateway.SubmitAttachmentAsync(request);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.NotSupported);
    }

    [Fact]
    public async Task ContentStore_ComputesChecksumAndSanitizesDisplayName()
    {
        var store = new InMemoryClaimAttachmentContentStore();
        var stored = await store.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha",
            TransmissionId = "tx1",
            AttachmentId = "att1",
            ContentType = "application/pdf",
            DisplayName = "../evil.pdf"
        }, new MemoryStream(PdfBytes));

        stored.ChecksumSha256.Should().Be(Convert.ToHexString(SHA256.HashData(PdfBytes)).ToLowerInvariant());
        stored.DisplayName.Should().Be("evil.pdf");
        stored.StorageKey.Should().NotContain("evil");
        await using var read = await store.OpenReadAsync(stored);
        using var ms = new MemoryStream();
        await read.CopyToAsync(ms);
        ms.ToArray().Should().Equal(PdfBytes);
    }
}
