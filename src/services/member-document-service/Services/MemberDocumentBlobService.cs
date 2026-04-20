using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace MemberDocumentService.Services;

public interface IMemberDocumentBlobService
{
    Task<long> UploadAsync(string container, string blobPath, Stream content, string contentType, IDictionary<string, string> tags, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string container, string blobPath, CancellationToken ct = default);
    Task SetTagsAsync(string container, string blobPath, IDictionary<string, string> tags, CancellationToken ct = default);
    Task<long> GetBlobSizeAsync(string container, string blobPath, CancellationToken ct = default);
    Uri GetBlobUri(string container, string blobPath);
    Uri? GenerateUploadSasUri(string container, string blobPath, string contentType, DateTimeOffset expiresAtUtc);
}

public class MemberDocumentBlobService : IMemberDocumentBlobService
{
    private readonly BlobServiceClient _blobServiceClient;

    public MemberDocumentBlobService(BlobServiceClient blobServiceClient)
    {
        _blobServiceClient = blobServiceClient;
    }

    public async Task<long> UploadAsync(string container, string blobPath, Stream content, string contentType, IDictionary<string, string> tags, CancellationToken ct = default)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(container);
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

        var blobClient = containerClient.GetBlobClient(blobPath);
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            },
            Tags = tags
        };

        await blobClient.UploadAsync(content, options, ct);
        var props = await blobClient.GetPropertiesAsync(cancellationToken: ct);
        return props.Value.ContentLength;
    }

    public async Task<Stream> DownloadAsync(string container, string blobPath, CancellationToken ct = default)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var response = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async Task SetTagsAsync(string container, string blobPath, IDictionary<string, string> tags, CancellationToken ct = default)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        await blobClient.SetTagsAsync(tags, cancellationToken: ct);
    }

    public async Task<long> GetBlobSizeAsync(string container, string blobPath, CancellationToken ct = default)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var props = await blobClient.GetPropertiesAsync(cancellationToken: ct);
        return props.Value.ContentLength;
    }

    public Uri GetBlobUri(string container, string blobPath)
    {
        return _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath).Uri;
    }

    public Uri? GenerateUploadSasUri(string container, string blobPath, string contentType, DateTimeOffset expiresAtUtc)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        if (!blobClient.CanGenerateSasUri)
        {
            return null;
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = container,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expiresAtUtc,
            ContentType = contentType
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);
        return blobClient.GenerateSasUri(sasBuilder);
    }
}
