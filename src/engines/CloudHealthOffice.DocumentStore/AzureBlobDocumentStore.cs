using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// Production <see cref="IDocumentStore"/> backed by Azure Blob Storage.
///
/// Registration (Program.cs / DI):
/// <code>
///   builder.Services.AddSingleton(new BlobServiceClient(connectionString));
///   builder.Services.AddSingleton&lt;IDocumentStore, AzureBlobDocumentStore&gt;();
/// </code>
/// </summary>
public class AzureBlobDocumentStore : IDocumentStore
{
    private readonly BlobServiceClient _client;
    private readonly ILogger<AzureBlobDocumentStore> _logger;

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public AzureBlobDocumentStore(
        BlobServiceClient client,
        ILogger<AzureBlobDocumentStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<DocumentUploadResult> UploadAsync(
        string container, string blobName,
        Stream content, string contentType,
        CancellationToken ct = default)
    {
        var containerClient = _client.GetBlobContainerClient(container);
        await containerClient.CreateIfNotExistsAsync(
            PublicAccessType.None, cancellationToken: ct);

        var blobClient = containerClient.GetBlobClient(blobName);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        };

        var sizeBytes = content.CanSeek ? content.Length : -1L;

        _logger.LogDebug("Uploading blob {Container}/{BlobName}", SanitizeForLog(container), SanitizeForLog(blobName));
        await blobClient.UploadAsync(content, options, ct);

        // Use actual size if stream was seekable; fall back to 0
        if (sizeBytes < 0 && blobClient.Uri is not null)
        {
            var props = await blobClient.GetPropertiesAsync(cancellationToken: ct);
            sizeBytes = props.Value.ContentLength;
        }

        return new DocumentUploadResult
        {
            Uri       = blobClient.Uri!,
            Container = container,
            BlobName  = blobName,
            SizeBytes = Math.Max(0, sizeBytes)
        };
    }

    public async Task<Stream> DownloadAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var blobClient = _client
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        try
        {
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new DocumentNotFoundException(container, blobName);
        }
    }

    public async Task<bool> ExistsAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var blobClient = _client
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        var result = await blobClient.ExistsAsync(ct);
        return result.Value;
    }

    public async Task DeleteAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var blobClient = _client
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public Uri GetUri(string container, string blobName) =>
        _client.GetBlobContainerClient(container).GetBlobClient(blobName).Uri;
}
