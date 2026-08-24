using System.Security.Cryptography;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Adapters;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using CloudHealthOffice.Infrastructure.Tests.Gateways;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class ClaimAttachmentReceiverTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] PdfBytes = "%PDF-1.4 inbound"u8.ToArray();

    private static (CloudHealthOfficeClaimAttachmentReceiver Receiver,
        InMemoryPayerClaimDirectory Claims,
        InMemoryInboundClaimAttachmentReceiptStore Receipts,
        InMemoryClaimAttachmentContentStore Content,
        CapturingMessageBus Bus)
        Harness(IInboundAttachmentScanner? scanner = null)
    {
        var claims = new InMemoryPayerClaimDirectory();
        var receipts = new InMemoryInboundClaimAttachmentReceiptStore();
        var content = new InMemoryClaimAttachmentContentStore();
        var bus = new CapturingMessageBus();
        var receiver = new CloudHealthOfficeClaimAttachmentReceiver(
            new PayerEligibilityRouter(new InMemoryPayerEligibilityDirectory()),
            claims,
            content,
            receipts,
            NullLogger<CloudHealthOfficeClaimAttachmentReceiver>.Instance,
            bus: bus,
            scanner: scanner);
        return (receiver, claims, receipts, content, bus);
    }

    private static InboundClaimAttachment Meta(
        string? claimId = ChoDemoClaimAttachmentSeed.ClaimId,
        string? payerId = ChoDemoEligibilitySeed.ExternalPayerId,
        string? claimedTenant = "untrusted-tenant",
        string? pccn = null,
        string? pcn = null,
        int? line = null,
        string contentType = "image/jpeg",
        string? ext = "ext-1",
        string? acn = "acn-1",
        ClaimAttachmentType type = ClaimAttachmentType.DentalImage) =>
        new()
        {
            PayerId = payerId,
            ClaimedTenantId = claimedTenant,
            ClaimId = claimId,
            ClaimControlNumber = pccn,
            PatientControlNumber = pcn,
            ServiceLineNumber = line,
            AttachmentControlNumber = acn,
            ExternalTransactionId = ext,
            AttachmentType = type,
            ContentType = contentType,
            Mode = ClaimAttachmentMode.Unsolicited
        };

    [Fact]
    public async Task ExactClaimId_MatchesAndDoesNotAdjudicate()
    {
        var (receiver, claims, receipts, _, bus) = Harness();
        var response = await receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes));

        response.IsSuccess.Should().BeTrue();
        response.Result!.Status.Should().Be(InboundClaimAttachmentStatus.AvailableToClaim);
        response.Result.ClaimId.Should().Be(ChoDemoClaimAttachmentSeed.ClaimId);
        response.Result.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
        response.Result.MatchingIdentifier.Should().Be("ClaimId");
        response.Result.AssociationMethod.Should().Be(InboundClaimAssociationMethod.Deterministic);
        response.Result.AvailableToExaminer.Should().BeTrue();
        response.Result.ClaimAdjudicated.Should().BeFalse();
        response.Result.ClaimPaid.Should().BeFalse();
        Convert.ToHexString(SHA256.HashData(JpegBytes)).ToLowerInvariant()
            .Should().Be(response.Result.ChecksumSha256);

        var stored = await claims.FindAsync(new PayerClaimLookup
        {
            TenantId = ChoDemoEligibilitySeed.TenantId,
            CanonicalPayerId = ChoDemoEligibilitySeed.CanonicalPayerId,
            ClaimId = ChoDemoClaimAttachmentSeed.ClaimId
        });
        stored.Unique!.Status.Should().Be(PayerDirectoryClaimStatus.Pended);
        stored.Unique.DocumentationReceived.Should().BeTrue();
        stored.Unique.IsPaid.Should().BeFalse();
        bus.Sent.Select(s => s.Options?.Properties?[InboundClaimAttachmentEventTopics.MessageTypeProperty])
            .Should().Contain(InboundClaimAttachmentMessageTypes.Received)
            .And.Contain(InboundClaimAttachmentMessageTypes.Matched);
        (await receipts.ListByClaimIdAsync(ChoDemoEligibilitySeed.TenantId, ChoDemoClaimAttachmentSeed.ClaimId))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task PayerClaimControlNumber_Matches()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(
            Meta(claimId: null, pccn: ChoDemoClaimAttachmentSeed.PayerClaimControlNumber),
            new MemoryStream(JpegBytes));
        response.Result!.ClaimId.Should().Be(ChoDemoClaimAttachmentSeed.ClaimId);
        response.Result.MatchingIdentifier.Should().Be("ClaimControlNumber");
    }

    [Fact]
    public async Task PatientControlNumber_MatchesWhenUnique()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(
            Meta(claimId: null, pcn: ChoDemoClaimAttachmentSeed.PatientControlNumber),
            new MemoryStream(JpegBytes));
        response.Result!.ClaimId.Should().Be(ChoDemoClaimAttachmentSeed.ClaimId);
        response.Result.MatchingIdentifier.Should().Be("PatientControlNumber");
    }

    [Fact]
    public async Task UnknownClaim_IsQuarantined()
    {
        var (receiver, _, receipts, _, bus) = Harness();
        var response = await receiver.ReceiveAsync(Meta(claimId: "NO-SUCH"), new MemoryStream(JpegBytes));
        response.Result!.Status.Should().Be(InboundClaimAttachmentStatus.Quarantined);
        response.Result.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatch);
        response.Result.AvailableToExaminer.Should().BeFalse();
        bus.Sent.Select(s => s.Options?.Properties?[InboundClaimAttachmentEventTopics.MessageTypeProperty])
            .Should().Contain(InboundClaimAttachmentMessageTypes.Quarantined);
        (await receipts.GetByIdAsync(response.Result.ReceiptId)).Should().NotBeNull();
    }

    [Fact]
    public async Task AmbiguousPatientControlNumber_IsQuarantined()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(
            Meta(claimId: null, pcn: ChoDemoClaimAttachmentSeed.AmbiguousPatientControlNumber),
            new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.AmbiguousClaim);
        response.Result.Status.Should().Be(InboundClaimAttachmentStatus.Quarantined);
    }

    [Fact]
    public async Task ClaimedTenantId_IsIgnored()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(
            Meta(claimedTenant: ChoDemoEligibilitySeed.OtherTenantId),
            new MemoryStream(JpegBytes));
        response.Result!.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
        response.Result.ClaimId.Should().Be(ChoDemoClaimAttachmentSeed.ClaimId);
    }

    [Fact]
    public async Task OtherTenantClaimId_DoesNotMatch()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(
            Meta(claimId: ChoDemoClaimAttachmentSeed.OtherTenantClaimId),
            new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatch);
    }

    [Fact]
    public async Task InvalidPayer_FailsExplicitly()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(Meta(payerId: "UNKNOWN-PAYER"), new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.InvalidPayer);
        response.Result.TenantId.Should().BeNull();
        response.Result.AssociationLevel.Should().Be(ClaimAttachmentAssociationLevel.None);
    }

    [Fact]
    public async Task AmbiguousPayer_FailsExplicitly()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(
            Meta(payerId: ChoDemoEligibilitySeed.AmbiguousExternalId),
            new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.AmbiguousPayer);
    }

    [Fact]
    public async Task ServiceLine_MatchesDeterministically()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(Meta(line: 2), new MemoryStream(JpegBytes));
        response.Result!.ServiceLineNumber.Should().Be(2);
        response.Result.AssociationLevel.Should().Be(ClaimAttachmentAssociationLevel.ServiceLine);
        response.Result.Status.Should().Be(InboundClaimAttachmentStatus.AvailableToClaim);
    }

    [Fact]
    public async Task InvalidServiceLine_DoesNotAttachToClaim()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(Meta(line: 99), new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceLineNotFound);
        response.Result.Status.Should().Be(InboundClaimAttachmentStatus.Quarantined);
        response.Result.AvailableToExaminer.Should().BeFalse();
        response.Result.ClaimId.Should().Be(ChoDemoClaimAttachmentSeed.ClaimId);
    }

    [Fact]
    public async Task DuplicateDelivery_DoesNotDuplicateStoreOrEvents()
    {
        var (receiver, _, receipts, content, bus) = Harness();
        var first = await receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes));
        var second = await receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes));
        second.Result!.Replay.Should().BeTrue();
        second.Result.ReceiptId.Should().Be(first.Result!.ReceiptId);
        (await receipts.ListByClaimIdAsync(ChoDemoEligibilitySeed.TenantId, ChoDemoClaimAttachmentSeed.ClaimId))
            .Should().HaveCount(1);
        content.Count.Should().Be(1);
        bus.Sent.Count(s => s.Options?.Properties?[InboundClaimAttachmentEventTopics.MessageTypeProperty]
            == InboundClaimAttachmentMessageTypes.Matched).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentIdentical_CreatesOneReceipt()
    {
        var (receiver, _, receipts, _, bus) = Harness();
        var tasks = Enumerable.Range(0, 12).Select(_ =>
            receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes)));
        var results = await Task.WhenAll(tasks);
        results.Select(r => r.Result!.ReceiptId).Distinct().Should().ContainSingle();
        results.Count(r => !r.Result!.Replay).Should().Be(1);
        (await receipts.ListByClaimIdAsync(ChoDemoEligibilitySeed.TenantId, ChoDemoClaimAttachmentSeed.ClaimId))
            .Should().HaveCount(1);
        bus.Sent.Count(s => s.Options?.Properties?[InboundClaimAttachmentEventTopics.MessageTypeProperty]
            == InboundClaimAttachmentMessageTypes.Received).Should().Be(1);
    }

    [Fact]
    public async Task SameTransactionDifferentChecksum_IsNewReceipt()
    {
        var (receiver, _, receipts, _, _) = Harness();
        await receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes));
        await receiver.ReceiveAsync(Meta(), new MemoryStream("%PDF-1.4 inbound-b"u8.ToArray()));
        (await receipts.ListByClaimIdAsync(ChoDemoEligibilitySeed.TenantId, ChoDemoClaimAttachmentSeed.ClaimId))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task SameChecksumDifferentControlNumber_IsNewReceipt()
    {
        var (receiver, _, receipts, _, _) = Harness();
        await receiver.ReceiveAsync(Meta(acn: "acn-a"), new MemoryStream(JpegBytes));
        await receiver.ReceiveAsync(Meta(acn: "acn-b"), new MemoryStream(JpegBytes));
        (await receipts.ListByClaimIdAsync(ChoDemoEligibilitySeed.TenantId, ChoDemoClaimAttachmentSeed.ClaimId))
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task UnsupportedMime_IsRejected()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(Meta(contentType: "application/zip"), new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.UnsupportedContentType);
        response.Result.AvailableToExaminer.Should().BeFalse();
    }

    [Fact]
    public async Task ZeroByte_IsRejected()
    {
        var (receiver, _, _, _, _) = Harness();
        var response = await receiver.ReceiveAsync(Meta(), new MemoryStream());
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
    }

    [Fact]
    public async Task ChecksumMismatch_IsQuarantined()
    {
        var (receiver, _, _, _, _) = Harness();
        var meta = Meta();
        meta.SuppliedChecksumSha256 = "deadbeef";
        var response = await receiver.ReceiveAsync(meta, new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.ChecksumMismatch);
        response.Result.Status.Should().Be(InboundClaimAttachmentStatus.Quarantined);
    }

    [Fact]
    public async Task UnsafeScan_IsNotAvailableToExaminer()
    {
        var (receiver, _, _, _, _) = Harness(new FixedScanner(ClaimAttachmentScanStatus.Quarantined));
        var response = await receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes));
        response.Result!.ErrorCategory.Should().Be(GatewayErrorCategory.AttachmentUnsafe);
        response.Result.AvailableToExaminer.Should().BeFalse();
    }

    [Fact]
    public async Task OutboxReplay_DoesNotDuplicateEvents()
    {
        var claims = new InMemoryPayerClaimDirectory();
        var receipts = new InMemoryInboundClaimAttachmentReceiptStore();
        var content = new InMemoryClaimAttachmentContentStore();
        var fail = new FailThenCaptureMessageBus(failFirstSends: 2);
        var receiver = new CloudHealthOfficeClaimAttachmentReceiver(
            new PayerEligibilityRouter(new InMemoryPayerEligibilityDirectory()),
            claims, content, receipts,
            NullLogger<CloudHealthOfficeClaimAttachmentReceiver>.Instance,
            bus: fail);
        await receiver.ReceiveAsync(Meta(), new MemoryStream(JpegBytes));
        fail.Sent.Should().BeEmpty();
        await receiver.DispatchPendingAsync(50);
        fail.Sent.Count(s => s.Options?.Properties?[InboundClaimAttachmentEventTopics.MessageTypeProperty]
            == InboundClaimAttachmentMessageTypes.Received).Should().Be(1);
        await receiver.DispatchPendingAsync(50);
        fail.Sent.Count(s => s.Options?.Properties?[InboundClaimAttachmentEventTopics.MessageTypeProperty]
            == InboundClaimAttachmentMessageTypes.Received).Should().Be(1);
    }

    [Fact]
    public async Task SameChecksum_DifferentTenant_DoesNotReplay()
    {
        var (receiver, _, receipts, _, _) = Harness();
        var first = await receiver.ReceiveAsync(
            Meta(ext: "shared-ext", acn: "shared-acn"),
            new MemoryStream(JpegBytes));
        var second = await receiver.ReceiveAsync(
            Meta(
                claimId: ChoDemoClaimAttachmentSeed.OtherTenantClaimId,
                payerId: ChoDemoEligibilitySeed.OtherExternalPayerId,
                ext: "shared-ext",
                acn: "shared-acn"),
            new MemoryStream(JpegBytes));
        first.Result!.Replay.Should().BeFalse();
        second.Result!.Replay.Should().BeFalse();
        second.Result.ReceiptId.Should().NotBe(first.Result.ReceiptId);
        second.Result.TenantId.Should().Be(ChoDemoEligibilitySeed.OtherTenantId);
        (await receipts.GetByIdAsync(first.Result.ReceiptId))!.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
    }

    [Fact]
    public async Task Persistence_SurvivesStoreCloneLookup()
    {
        var store = new InMemoryInboundClaimAttachmentReceiptStore();
        var record = new InboundClaimAttachmentReceipt
        {
            TenantId = "cho-demo",
            ClaimId = "CLM-DEMO-275-001",
            IdempotencyKey = "k1",
            ChecksumSha256 = "abc"
        };
        (await store.TryCreateAsync(record)).Created.Should().BeTrue();
        (await store.TryCreateAsync(record)).Created.Should().BeFalse();
        (await store.GetByIdempotencyKeyAsync("k1"))!.ClaimId.Should().Be("CLM-DEMO-275-001");
    }

    [Fact]
    public void StediInboundAdapter_IsNotImplemented()
    {
        var adapter = new StediInboundClaimAttachmentAdapter();
        adapter.IsImplemented.Should().BeFalse();
        var act = () => adapter.EnsureImplemented();
        act.Should().Throw<NotSupportedException>().WithMessage("*not publicly available*");
    }

    [Fact]
    public void X12InboundAdapter_IsDeferred()
    {
        new X12InboundClaimAttachmentAdapter().IsImplemented.Should().BeFalse();
    }

    private sealed class FixedScanner : IInboundAttachmentScanner
    {
        private readonly ClaimAttachmentScanStatus _status;
        public FixedScanner(ClaimAttachmentScanStatus status) => _status = status;
        public Task<ClaimAttachmentScanStatus> EvaluateAsync(
            ClaimAttachmentContentReference content, CancellationToken ct = default) =>
            Task.FromResult(_status);
    }
}
