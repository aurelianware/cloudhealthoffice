using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Models;

/// <summary>
/// Vendor-neutral inbound 275-equivalent attachment. Transport adapters
/// translate Stedi / X12 / API payloads into this shape. Bytes are not on
/// this object — only a content reference after storage.
///
/// <see cref="ClaimedTenantId"/> is untrusted and is never used for routing.
/// </summary>
public sealed class InboundClaimAttachment
{
    public string? InboundAttachmentId { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? CorrelationId { get; set; }

    public string? ClaimedTenantId { get; set; }

    public string? PayerId { get; set; }

    public string? TradingPartnerId { get; set; }

    public string? AuthenticatedEndpointId { get; set; }

    public string? AdapterName { get; set; }

    public string? ClaimId { get; set; }

    public string? ClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string? ServiceLineControlNumber { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public string? PayerRequestControlNumber { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; } = ClaimAttachmentType.Other;

    public ClaimAttachmentMode Mode { get; set; } = ClaimAttachmentMode.Unsolicited;

    public string? FileName { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long ContentLength { get; set; }

    public ClaimAttachmentContentReference? Content { get; set; }

    public string? SuppliedChecksumSha256 { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public string? Source { get; set; }
}

public enum InboundClaimAttachmentStatus
{
    Received = 1,
    Stored = 2,
    PendingValidation = 3,
    Validated = 4,
    Matched = 5,
    AvailableToClaim = 6,
    Quarantined = 7,
    Rejected = 8,
    Failed = 9
}

public enum InboundClaimAssociationMethod
{
    None = 0,
    Deterministic = 1
}

public sealed class InboundClaimAttachmentResult
{
    public string ReceiptId { get; set; } = string.Empty;

    public InboundClaimAttachmentStatus Status { get; set; }

    public string? TenantId { get; set; }

    public string? CanonicalPayerId { get; set; }

    public string? ClaimId { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; }

    public ClaimAttachmentAssociationLevel AssociationLevel { get; set; }

    public InboundClaimAssociationMethod AssociationMethod { get; set; }

    public string? MatchingIdentifier { get; set; }

    public string? ContentType { get; set; }

    public long ContentLength { get; set; }

    public string? ChecksumSha256 { get; set; }

    public string? ContentStorageKey { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public bool Replay { get; set; }

    public bool AvailableToExaminer { get; set; }

    public bool ClaimAdjudicated { get; set; }

    public bool ClaimPaid { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; }

    public string? ErrorMessage { get; set; }
}
