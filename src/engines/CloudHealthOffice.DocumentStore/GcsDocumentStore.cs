using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// <see cref="IDocumentStore"/> backed by Google Cloud Storage.
///
/// One bucket per deployment (<c>DocumentStore:Gcs:Bucket</c>), created by
/// Terraform, never by this class; each CHO container is a prefix inside it
/// (see <see cref="DocumentKeys"/>). Tags are the object's custom metadata,
/// since GCS has no separate tag store. GCS lifecycle rules cannot match
/// custom metadata, so on this provider the tags record retention and legal
/// hold but the storage tier does not act on them; the document service
/// enforces both (object holds and bucket lock are later steps).
///
/// Credentials come from Application Default Credentials. On GKE that is
/// Workload Identity, a compute credential, so upload links are signed by the
/// IAM signBlob API: the service account needs
/// <c>roles/iam.serviceAccountTokenCreator</c> on itself. With no signing
/// credential (an emulator, a user login) <see cref="CreateUploadLinkAsync"/>
/// returns null.
/// </summary>
public sealed class GcsDocumentStore : IDocumentStore
{
    private readonly StorageClient _client;
    private readonly UrlSigner? _signer;
    private readonly string _bucket;
    private readonly ILogger<GcsDocumentStore> _logger;

    public GcsDocumentStore(
        StorageClient client,
        UrlSigner? signer,
        string bucket,
        ILogger<GcsDocumentStore> logger)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("A bucket name is required.", nameof(bucket));

        _client = client;
        _signer = signer;
        _bucket = bucket;
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
        var objectName = DocumentKeys.ObjectName(container, blobName);
        var destination = new GcsObject
        {
            Bucket = _bucket,
            Name = objectName,
            ContentType = contentType,
            Metadata = tags is null ? null : new Dictionary<string, string>(tags)
        };

        _logger.LogDebug("Uploading object {Bucket}/{ObjectName}",
            DocumentKeys.SanitizeForLog(_bucket), DocumentKeys.SanitizeForLog(objectName));
        var stored = await _client.UploadObjectAsync(destination, content, cancellationToken: ct);

        return new DocumentUploadResult
        {
            Uri       = GetUri(container, blobName),
            Container = container,
            BlobName  = blobName,
            SizeBytes = (long)(stored.Size ?? 0)
        };
    }

    public async Task<Stream> DownloadAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var objectName = DocumentKeys.ObjectName(container, blobName);

        // The client library writes to a destination stream rather than handing
        // one back. A temp file (deleted on close) keeps large documents out of
        // memory and gives the caller a seekable stream.
        var buffer = new FileStream(
            Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await _client.DownloadObjectAsync(_bucket, objectName, buffer, cancellationToken: ct);
            buffer.Position = 0;
            return buffer;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await buffer.DisposeAsync();
            throw new DocumentNotFoundException(container, blobName);
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> ExistsAsync(
        string container, string blobName,
        CancellationToken ct = default) =>
        await TryGetObjectAsync(DocumentKeys.ObjectName(container, blobName), ct) is not null;

    public async Task DeleteAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_bucket, DocumentKeys.ObjectName(container, blobName), cancellationToken: ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    public async Task SetTagsAsync(
        string container, string blobName,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        var objectName = DocumentKeys.ObjectName(container, blobName);

        // Update replaces all metadata (patch would merge, leaving stale keys).
        // The metageneration precondition stops a concurrent change from being
        // silently overwritten; on a conflict the read-modify-write is retried,
        // since the new tags replace the old ones whatever they were.
        for (var attempt = 1; ; attempt++)
        {
            var current = await TryGetObjectAsync(objectName, ct)
                ?? throw new DocumentNotFoundException(container, blobName);
            current.Metadata = new Dictionary<string, string>(tags);
            try
            {
                await _client.UpdateObjectAsync(current,
                    new UpdateObjectOptions { IfMetagenerationMatch = current.Metageneration },
                    ct);
                return;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                throw new DocumentNotFoundException(container, blobName);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.PreconditionFailed && attempt < MaxTagAttempts)
            {
                _logger.LogDebug("Object {ObjectName} changed while its tags were set; retrying",
                    DocumentKeys.SanitizeForLog(objectName));
            }
        }
    }

    private const int MaxTagAttempts = 3;

    public async Task<DocumentProperties> GetPropertiesAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var stored = await TryGetObjectAsync(DocumentKeys.ObjectName(container, blobName), ct)
            ?? throw new DocumentNotFoundException(container, blobName);

        return new DocumentProperties
        {
            SizeBytes   = (long)(stored.Size ?? 0),
            ContentType = stored.ContentType,
            Tags        = stored.Metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(stored.Metadata)
        };
    }

    public async Task<DocumentUploadLink?> CreateUploadLinkAsync(
        string container, string blobName, string contentType,
        DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        if (_signer is null)
        {
            return null;
        }

        var template = UrlSigner.RequestTemplate
            .FromBucket(_bucket)
            .WithObjectName(DocumentKeys.ObjectName(container, blobName))
            .WithHttpMethod(HttpMethod.Put)
            .WithContentHeaders(new Dictionary<string, IEnumerable<string>>
            {
                ["Content-Type"] = new[] { contentType }
            });
        var url = await _signer.SignAsync(template, UrlSigner.Options.FromExpiration(expiresAtUtc), ct);

        return new DocumentUploadLink
        {
            Url = new Uri(url),
            Headers = new Dictionary<string, string> { ["Content-Type"] = contentType },
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public Uri GetUri(string container, string blobName) =>
        new($"gs://{_bucket}/{DocumentKeys.ObjectName(container, blobName)}");

    private async Task<GcsObject?> TryGetObjectAsync(string objectName, CancellationToken ct)
    {
        try
        {
            return await _client.GetObjectAsync(_bucket, objectName, cancellationToken: ct);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
