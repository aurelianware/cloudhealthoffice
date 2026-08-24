using System.Collections.Concurrent;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Process-local attachment bytes for Development and tests. Production hosts
/// should wrap the existing <c>IDocumentStore</c> (Azure Blob).
/// </summary>
public sealed class InMemoryClaimAttachmentContentStore : IClaimAttachmentContentStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly ClaimAttachmentOptions _options;

    public InMemoryClaimAttachmentContentStore(ClaimAttachmentOptions? options = null)
    {
        _options = options ?? new ClaimAttachmentOptions();
    }

    public async Task<ClaimAttachmentContentReference> StoreAsync(
        ClaimAttachmentStoreRequest request,
        Stream content,
        CancellationToken ct = default)
    {
        await using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct).ConfigureAwait(false);
        var bytes = copy.ToArray();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Attachment content length must be greater than zero.");
        }

        if (bytes.Length > _options.EffectiveMaxBytes())
        {
            throw new InvalidOperationException("Attachment exceeds the configured maximum size.");
        }

        copy.Position = 0;
        var checksum = await ClaimAttachmentRules.ComputeSha256HexAsync(copy, ct).ConfigureAwait(false);
        var contentType = ClaimAttachmentRules.NormalizeContentType(request.ContentType);
        var storageKey = ClaimAttachmentRules.StorageKey(
            request.TenantId, request.TransmissionId, request.AttachmentId, checksum, contentType);
        var container = string.IsNullOrWhiteSpace(_options.ContentContainer)
            ? "claim-attachments"
            : _options.ContentContainer;
        _blobs[$"{container}/{storageKey}"] = bytes;

        return new ClaimAttachmentContentReference
        {
            Container = container,
            StorageKey = storageKey,
            ContentType = contentType,
            ContentLength = bytes.Length,
            ChecksumSha256 = checksum,
            ScanStatus = request.ScanStatus,
            DisplayName = ClaimAttachmentRules.SanitizeFileName(request.DisplayName)
        };
    }

    public Task<Stream> OpenReadAsync(ClaimAttachmentContentReference reference, CancellationToken ct = default)
    {
        if (!_blobs.TryGetValue(Key(reference), out var bytes))
        {
            throw new InvalidOperationException("Attachment content was not found.");
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Task<bool> ExistsAsync(ClaimAttachmentContentReference reference, CancellationToken ct = default) =>
        Task.FromResult(_blobs.ContainsKey(Key(reference)));

    public byte[]? GetBytes(ClaimAttachmentContentReference reference) =>
        _blobs.TryGetValue(Key(reference), out var bytes) ? bytes : null;

    public int Count => _blobs.Count;

    private static string Key(ClaimAttachmentContentReference reference) =>
        $"{reference.Container}/{reference.StorageKey}";
}
