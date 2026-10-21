using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// <see cref="IDocumentStore"/> backed by Azure Blob Storage. Each CHO
/// container is an Azure container, created on first upload. Tags are blob
/// index tags. Upload links are blob SAS tokens, which need account-key
/// credentials (a connection string with an AccountKey); otherwise
/// <see cref="CreateUploadLinkAsync"/> returns null.
///
/// Registered by <c>AddChoDocumentStore</c> when <c>DocumentStore:Provider</c>
/// is <c>Azure</c> (or unset with <c>BlobStorage:ConnectionString</c> present).
/// </summary>
public class AzureBlobDocumentStore : IDocumentStore
{
    private readonly BlobServiceClient _client;
    private readonly ILogger<AzureBlobDocumentStore> _logger;

    public AzureBlobDocumentStore(
        BlobServiceClient client,
        ILogger<AzureBlobDocumentStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task<DocumentUploadResult> UploadAsync(
        string container, string blobName,
        Stream content, string contentType,
        CancellationToken ct = default) =>
        UploadAsync(container, blobName, content, contentType, null, ct);

    public async Task<DocumentUploadResult> UploadAsync(
        string container, string blobName,
        Stream content, string contentType,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken ct = default)
    {
        var blobClient = Blob(container, blobName);
        await _client.GetBlobContainerClient(container).CreateIfNotExistsAsync(
            PublicAccessType.None, cancellationToken: ct);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
            Tags = tags is null ? null : new Dictionary<string, string>(tags)
        };

        _logger.LogDebug("Uploading blob {Container}/{BlobName}", DocumentKeys.SanitizeForLog(container), DocumentKeys.SanitizeForLog(blobName));
        await blobClient.UploadAsync(content, options, ct);

        // The stored size, not the stream's: a non-seekable stream has no length.
        var props = await blobClient.GetPropertiesAsync(cancellationToken: ct);

        return new DocumentUploadResult
        {
            Uri       = blobClient.Uri,
            Container = container,
            BlobName  = blobName,
            SizeBytes = props.Value.ContentLength
        };
    }

    public async Task<Stream> DownloadAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var blobClient = Blob(container, blobName);

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
        var result = await Blob(container, blobName).ExistsAsync(ct);
        return result.Value;
    }

    public async Task DeleteAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        await Blob(container, blobName).DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task SetTagsAsync(
        string container, string blobName,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        try
        {
            await Blob(container, blobName).SetTagsAsync(new Dictionary<string, string>(tags), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new DocumentNotFoundException(container, blobName);
        }
    }

    public async Task<DocumentProperties> GetPropertiesAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var blobClient = Blob(container, blobName);
        try
        {
            var props = await blobClient.GetPropertiesAsync(cancellationToken: ct);
            var tags = props.Value.TagCount > 0
                ? (await blobClient.GetTagsAsync(cancellationToken: ct)).Value.Tags
                : new Dictionary<string, string>();

            return new DocumentProperties
            {
                SizeBytes   = props.Value.ContentLength,
                ContentType = props.Value.ContentType,
                Tags        = new Dictionary<string, string>(tags)
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new DocumentNotFoundException(container, blobName);
        }
    }

    public async Task<DocumentUploadLink?> CreateUploadLinkAsync(
        string container, string blobName, string contentType,
        DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        var blobClient = Blob(container, blobName);
        var containerClient = _client.GetBlobContainerClient(container);
        if (!blobClient.CanGenerateSasUri)
        {
            return null;
        }

        // The client PUTs straight to storage, so the container must already exist.
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = container,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = expiresAtUtc,
            // HTTPS only, except a plain-HTTP development endpoint (Azurite).
            Protocol = blobClient.Uri.Scheme == Uri.UriSchemeHttp ? SasProtocol.HttpsAndHttp : SasProtocol.Https,
            ContentType = contentType
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

        return new DocumentUploadLink
        {
            Url = blobClient.GenerateSasUri(sasBuilder),
            Headers = new Dictionary<string, string>
            {
                ["x-ms-blob-type"] = "BlockBlob",
                ["Content-Type"] = contentType
            },
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public Uri GetUri(string container, string blobName) => Blob(container, blobName).Uri;

    private BlobClient Blob(string container, string blobName)
    {
        // Same naming rules as the bucket providers: a '..' segment would be
        // normalised away by the URI and could reach another container.
        DocumentKeys.ObjectName(container, blobName);
        return _client.GetBlobContainerClient(container).GetBlobClient(blobName);
    }
}
