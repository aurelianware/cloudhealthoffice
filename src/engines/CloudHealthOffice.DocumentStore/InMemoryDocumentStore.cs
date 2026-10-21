using System.Collections.Concurrent;

namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// In-memory <see cref="IDocumentStore"/> for unit tests and local development.
///
/// Thread-safe. Documents are stored as byte arrays keyed by
/// "{container}/{blobName}". URIs use the scheme "memory://". It cannot sign
/// upload links (<see cref="CreateUploadLinkAsync"/> returns null), like a
/// real store whose credentials cannot sign.
///
/// Usage in tests:
/// <code>
///   var store = new InMemoryDocumentStore();
///   // inject into system under test
///   var bytes = store.GetBytes("attachments", "tenant/claims/c1/att1.pdf");
/// </code>
/// </summary>
public class InMemoryDocumentStore : IDocumentStore
{
    private sealed record Entry(byte[] Bytes, string ContentType, IReadOnlyDictionary<string, string> Tags);

    private readonly ConcurrentDictionary<string, Entry> _blobs = new();

    private static string Key(string container, string blobName) =>
        $"{container}/{blobName}";

    public Task<DocumentUploadResult> UploadAsync(
        string container, string blobName,
        Stream content, string contentType,
        CancellationToken ct = default) =>
        UploadAsync(container, blobName, content, contentType, null, ct);

    public Task<DocumentUploadResult> UploadAsync(
        string container, string blobName,
        Stream content, string contentType,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var bytes = ms.ToArray();

        _blobs[Key(container, blobName)] = new Entry(bytes, contentType, DocumentKeys.Copy(tags));

        return Task.FromResult(new DocumentUploadResult
        {
            Uri       = GetUri(container, blobName),
            Container = container,
            BlobName  = blobName,
            SizeBytes = bytes.Length
        });
    }

    public Task<Stream> DownloadAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        if (!_blobs.TryGetValue(Key(container, blobName), out var entry))
            throw new DocumentNotFoundException(container, blobName);

        return Task.FromResult<Stream>(new MemoryStream(entry.Bytes, writable: false));
    }

    public Task<bool> ExistsAsync(
        string container, string blobName,
        CancellationToken ct = default) =>
        Task.FromResult(_blobs.ContainsKey(Key(container, blobName)));

    public Task DeleteAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        _blobs.TryRemove(Key(container, blobName), out _);
        return Task.CompletedTask;
    }

    public Task SetTagsAsync(
        string container, string blobName,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        var key = Key(container, blobName);
        while (true)
        {
            if (!_blobs.TryGetValue(key, out var entry))
                throw new DocumentNotFoundException(container, blobName);
            if (_blobs.TryUpdate(key, entry with { Tags = DocumentKeys.Copy(tags) }, entry))
                return Task.CompletedTask;
        }
    }

    public Task<DocumentProperties> GetPropertiesAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        if (!_blobs.TryGetValue(Key(container, blobName), out var entry))
            throw new DocumentNotFoundException(container, blobName);

        return Task.FromResult(new DocumentProperties
        {
            SizeBytes   = entry.Bytes.Length,
            ContentType = entry.ContentType,
            Tags        = DocumentKeys.Copy(entry.Tags)
        });
    }

    public Task<DocumentUploadLink?> CreateUploadLinkAsync(
        string container, string blobName, string contentType,
        DateTimeOffset expiresAtUtc, CancellationToken ct = default) =>
        Task.FromResult<DocumentUploadLink?>(null);

    public Uri GetUri(string container, string blobName) =>
        new($"memory://{container}/{blobName}");

    // ── Test helpers ─────────────────────────────────────────────────────

    /// <summary>Return the raw bytes stored for a blob, or null if not found.</summary>
    public byte[]? GetBytes(string container, string blobName) =>
        _blobs.TryGetValue(Key(container, blobName), out var entry) ? entry.Bytes : null;

    /// <summary>Return the tags stored for a blob, or null if not found.</summary>
    public IReadOnlyDictionary<string, string>? GetTags(string container, string blobName) =>
        _blobs.TryGetValue(Key(container, blobName), out var entry) ? entry.Tags : null;

    /// <summary>Number of blobs currently stored.</summary>
    public int Count => _blobs.Count;

    /// <summary>Clear all stored blobs.</summary>
    public void Clear() => _blobs.Clear();
}
