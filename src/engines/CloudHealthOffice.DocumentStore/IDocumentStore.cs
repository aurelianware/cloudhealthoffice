namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// Provider-agnostic abstraction for binary document storage.
///
/// Implementations:
///   AzureBlobDocumentStore  — Azure Blob Storage
///   GcsDocumentStore        — Google Cloud Storage (one bucket; containers are prefixes)
///   S3DocumentStore         — Amazon S3 or an S3-compatible store (one bucket; containers are prefixes)
///   InMemoryDocumentStore   — tests and local development
///
/// Services register the configured one with <c>AddChoDocumentStore</c>
/// (<see cref="DocumentStoreServiceCollectionExtensions"/>). Callers never see
/// provider SDK types: a missing document is always
/// <see cref="DocumentNotFoundException"/>.
///
/// Tags are small string labels kept with the stored bytes (retention policy,
/// retention date, legal hold), so the storage tier's own lifecycle rules can
/// act on them. Azure keeps them as blob index tags, Google Cloud Storage as
/// object metadata and S3 as object tags (S3 allows at most 10 per object).
/// </summary>
public interface IDocumentStore
{
    /// <summary>
    /// Upload a document stream and return metadata about the stored object.
    /// An existing object at the same name is replaced. Azure creates the
    /// container if needed; the bucket-backed providers write under the
    /// container's prefix in their configured bucket.
    /// </summary>
    Task<DocumentUploadResult> UploadAsync(
        string container,
        string blobName,
        Stream content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>Upload a document stream with tags. See <see cref="UploadAsync(string, string, Stream, string, CancellationToken)"/>.</summary>
    Task<DocumentUploadResult> UploadAsync(
        string container,
        string blobName,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken ct = default);

    /// <summary>
    /// Download a document as a stream. The caller is responsible for disposing the stream.
    /// Throws <see cref="DocumentNotFoundException"/> if the document does not exist.
    /// </summary>
    Task<Stream> DownloadAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>Return true if the named document exists in the container.</summary>
    Task<bool> ExistsAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>Delete a document. No-ops silently if the document does not exist.</summary>
    Task DeleteAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>
    /// Replace the document's tags with <paramref name="tags"/>.
    /// Throws <see cref="DocumentNotFoundException"/> if the document does not exist.
    /// </summary>
    Task SetTagsAsync(
        string container,
        string blobName,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default);

    /// <summary>
    /// Size, content type and tags of a stored document.
    /// Throws <see cref="DocumentNotFoundException"/> if the document does not exist.
    /// </summary>
    Task<DocumentProperties> GetPropertiesAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>
    /// A short-lived, write-only link that lets a client upload exactly one
    /// document (this container and name) directly to storage, until
    /// <paramref name="expiresAtUtc"/>. The link grants no read access.
    ///
    /// Returns null when the provider's credentials cannot sign links (an
    /// Azure connection without an account key, an emulator, the in-memory
    /// store). Callers answer that case themselves; they never fall back to a
    /// broader credential.
    /// </summary>
    Task<DocumentUploadLink?> CreateUploadLinkAsync(
        string container,
        string blobName,
        string contentType,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Return the canonical URI for a document without making a network call.
    /// Not a download link: callers serve content through their own API.
    /// </summary>
    Uri GetUri(string container, string blobName);
}
