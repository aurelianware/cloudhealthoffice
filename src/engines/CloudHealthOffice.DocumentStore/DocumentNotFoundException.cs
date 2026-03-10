namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// Thrown by <see cref="IDocumentStore.DownloadAsync"/> when the requested
/// blob does not exist in the backing store.
/// </summary>
public class DocumentNotFoundException : Exception
{
    public string Container { get; }
    public string BlobName { get; }

    public DocumentNotFoundException(string container, string blobName)
        : base($"Document not found: {container}/{blobName}")
    {
        Container = container;
        BlobName  = blobName;
    }
}
