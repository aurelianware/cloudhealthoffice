namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// Provider-agnostic abstraction for binary document storage.
///
/// Implementations:
///   AzureBlobDocumentStore  — production (Azure Blob Storage)
///   InMemoryDocumentStore   — tests and local development
///
/// Why an abstraction instead of direct BlobServiceClient usage:
///   - Decouples service logic from Azure SDK types so unit tests do not
///     require a live storage account or Azurite
///   - Allows a future swap to S3, GCS, or on-prem without touching callers
///   - Centralises retry, logging, and container-creation policies in one place
/// </summary>
public interface IDocumentStore
{
    /// <summary>
    /// Upload a document stream and return metadata about the stored blob.
    /// The container is created automatically if it does not exist.
    /// </summary>
    Task<DocumentUploadResult> UploadAsync(
        string container,
        string blobName,
        Stream content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Download a document as a stream. The caller is responsible for disposing the stream.
    /// Throws <see cref="DocumentNotFoundException"/> if the blob does not exist.
    /// </summary>
    Task<Stream> DownloadAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>Return true if the named blob exists in the container.</summary>
    Task<bool> ExistsAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>Delete a blob. No-ops silently if the blob does not exist.</summary>
    Task DeleteAsync(
        string container,
        string blobName,
        CancellationToken ct = default);

    /// <summary>
    /// Return the canonical URI for a blob without making a network call.
    /// For the in-memory provider this returns a scheme-less pseudo-URI.
    /// </summary>
    Uri GetUri(string container, string blobName);
}
