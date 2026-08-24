using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Durable 275 attachment-transmission record. Does not store file bytes.
/// Lifecycle is independent of 837 / 277CA / adjudication / payment.
/// </summary>
public interface IClaimAttachmentTransmissionStore
{
    Task<ClaimAttachmentTransmissionRecord?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default);

    Task<ClaimAttachmentTransmissionRecord?> GetByIdAsync(
        string attachmentTransmissionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimAttachmentTransmissionRecord>> ListByClaimTransmissionIdAsync(
        string claimTransmissionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimAttachmentTransmissionRecord>> FindByChecksumAsync(
        string tenantId, string checksumSha256, CancellationToken ct = default);

    Task SaveAsync(ClaimAttachmentTransmissionRecord record, CancellationToken ct = default);

    Task<(bool Created, ClaimAttachmentTransmissionRecord Record)> TryCreateAsync(
        ClaimAttachmentTransmissionRecord record, CancellationToken ct = default);
}

public sealed class ClaimAttachmentTransmissionRecord
{
    public string AttachmentTransmissionId { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public string ClaimTransmissionId { get; set; } = string.Empty;

    public string AttachmentId { get; set; } = string.Empty;

    public int AttachmentVersion { get; set; } = 1;

    public string GatewayName { get; set; } = string.Empty;

    public string? PayerId { get; set; }

    public GatewayClaimType ClaimType { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; }

    public ClaimAttachmentMode Mode { get; set; } = ClaimAttachmentMode.Unsolicited;

    public ClaimAttachmentAssociationLevel AssociationLevel { get; set; } =
        ClaimAttachmentAssociationLevel.Claim;

    public int? ServiceLineNumber { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long ContentLength { get; set; }

    public string? ChecksumSha256 { get; set; }

    public string? ContentContainer { get; set; }

    public string? ContentStorageKey { get; set; }

    public string? ExternalTransactionId { get; set; }

    public ClaimAttachmentTransmissionStatus Status { get; set; } =
        ClaimAttachmentTransmissionStatus.Stored;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? CorrelationId { get; set; }

    public DateTimeOffset SubmittedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public int RetryCount { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; } = GatewayErrorCategory.None;

    public string? ErrorMessage { get; set; }
}
