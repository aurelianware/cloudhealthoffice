using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders;

public interface IInboundClaimAttachmentReceiptStore
{
    Task<InboundClaimAttachmentReceipt?> GetByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default);

    Task<InboundClaimAttachmentReceipt?> GetByIdAsync(
        string receiptId, CancellationToken ct = default);

    Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default);

    Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListPendingOutboxAsync(
        int take, CancellationToken ct = default);

    Task SaveAsync(InboundClaimAttachmentReceipt record, CancellationToken ct = default);

    Task<(bool Created, InboundClaimAttachmentReceipt Record)> TryCreateAsync(
        InboundClaimAttachmentReceipt record, CancellationToken ct = default);
}

public sealed class InboundClaimAttachmentReceipt
{
    public string ReceiptId { get; set; } = Guid.NewGuid().ToString("N");

    public string IdempotencyKey { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string? CanonicalPayerId { get; set; }

    public string? ClaimId { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; }

    public ClaimAttachmentMode Mode { get; set; } = ClaimAttachmentMode.Unsolicited;

    public string? ContentType { get; set; }

    public long ContentLength { get; set; }

    public string? ChecksumSha256 { get; set; }

    public string? ContentContainer { get; set; }

    public string? ContentStorageKey { get; set; }

    public string SourceAdapter { get; set; } = string.Empty;

    public InboundClaimAttachmentStatus Status { get; set; } = InboundClaimAttachmentStatus.Received;

    public InboundClaimAssociationMethod AssociationMethod { get; set; }

    public string? MatchingIdentifier { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public DateTimeOffset? MatchedAtUtc { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; }

    public string? ErrorMessage { get; set; }

    public List<InboundAttachmentOutboxEntry> Outbox { get; set; } = new();

    public bool HasPendingOutbox => Outbox.Any(e => e.PublishedAtUtc is null);
}

public sealed class InboundAttachmentOutboxEntry
{
    public string EventType { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}

public static class InboundClaimAttachmentEventTopics
{
    public const string TopicName = "claim-attachment-inbound-events";

    public const string MessageTypeProperty = "MessageType";
}

public static class InboundClaimAttachmentMessageTypes
{
    public const string Received = "ClaimAttachmentReceived";

    public const string Matched = "ClaimAttachmentMatched";

    public const string Quarantined = "ClaimAttachmentQuarantined";
}

public sealed class InboundClaimAttachmentAuditMessage
{
    public string MessageType { get; set; } = string.Empty;

    public string ReceiptId { get; set; } = string.Empty;

    public string? TenantId { get; set; }

    public string? ClaimId { get; set; }

    public InboundClaimAttachmentStatus Status { get; set; }

    public string? Adapter { get; set; }

    public string? ContentType { get; set; }

    public long ContentLength { get; set; }

    public string? ChecksumPrefix { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; }

    public bool Replay { get; set; }
}
