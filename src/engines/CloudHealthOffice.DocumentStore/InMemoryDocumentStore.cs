using System.Collections.Concurrent;

namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// In-memory <see cref="IDocumentStore"/> for unit tests and local development.
///
/// Thread-safe. Documents are stored as byte arrays keyed by
/// "{container}/{blobName}". URIs use the scheme "memory://".
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
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    private static string Key(string container, string blobName) =>
        $"{container}/{blobName}";

    public Task<DocumentUploadResult> UploadAsync(
        string container, string blobName,
        Stream content, string contentType,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var bytes = ms.ToArray();

        _blobs[Key(container, blobName)] = bytes;

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
        if (!_blobs.TryGetValue(Key(container, blobName), out var bytes))
            throw new DocumentNotFoundException(container, blobName);

        return Task.FromResult<Stream>(new MemoryStream(bytes));
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

    public Uri GetUri(string container, string blobName) =>
        new($"memory://{container}/{blobName}");

    // ── Test helpers ─────────────────────────────────────────────────────

    /// <summary>Return the raw bytes stored for a blob, or null if not found.</summary>
    public byte[]? GetBytes(string container, string blobName) =>
        _blobs.TryGetValue(Key(container, blobName), out var bytes) ? bytes : null;

    /// <summary>Number of blobs currently stored.</summary>
    public int Count => _blobs.Count;

    /// <summary>Clear all stored blobs.</summary>
    public void Clear() => _blobs.Clear();
}
