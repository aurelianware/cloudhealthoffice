using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Secure attachment-content store. Bytes live here, not on claim or
/// transmission aggregates. The shape matches CHO <c>IDocumentStore</c>
/// (container + storage key) so a host can wrap Azure Blob without a second
/// storage provider.
/// </summary>
public interface IClaimAttachmentContentStore
{
    Task<ClaimAttachmentContentReference> StoreAsync(
        ClaimAttachmentStoreRequest request,
        Stream content,
        CancellationToken ct = default);

    Task<Stream> OpenReadAsync(ClaimAttachmentContentReference reference, CancellationToken ct = default);

    Task<bool> ExistsAsync(ClaimAttachmentContentReference reference, CancellationToken ct = default);
}

public sealed class ClaimAttachmentStoreRequest
{
    public string TenantId { get; set; } = string.Empty;

    public string TransmissionId { get; set; } = string.Empty;

    public string AttachmentId { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public ClaimAttachmentScanStatus ScanStatus { get; set; } = ClaimAttachmentScanStatus.Unknown;
}
