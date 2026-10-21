using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// <see cref="IDocumentStore"/> backed by Amazon S3, or an S3-compatible
/// store (MinIO in tests) through <c>DocumentStore:S3:ServiceUrl</c>.
///
/// One bucket per deployment (<c>DocumentStore:S3:Bucket</c>), created by the
/// deployment, never by this class; each CHO container is a prefix inside it
/// (see <see cref="DocumentKeys"/>). Tags are S3 object tags, which lifecycle
/// rules can filter on; S3 allows at most 10 per object.
///
/// Credentials come from the SDK's default chain (an IAM role for service
/// accounts on EKS, instance profile, or environment). Upload links are
/// presigned PUT URLs signed with those credentials.
/// </summary>
public sealed class S3DocumentStore : IDocumentStore
{
    /// <summary>S3's limit on tags per object.</summary>
    public const int MaxTags = 10;

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly ILogger<S3DocumentStore> _logger;

    public S3DocumentStore(IAmazonS3 client, string bucket, ILogger<S3DocumentStore> logger)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("A bucket name is required.", nameof(bucket));

        _client = client;
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
        var key = DocumentKeys.ObjectName(container, blobName);

        // PutObject needs the length up front; a non-seekable stream is buffered
        // to a temp file (deleted on close) rather than into memory.
        await using var temp = content.CanSeek ? null : await BufferAsync(content, ct);
        var body = temp ?? content;

        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = body,
            ContentType = contentType,
            AutoCloseStream = false,
            TagSet = ToTagSet(tags)
        };

        _logger.LogDebug("Uploading object {Bucket}/{Key}",
            DocumentKeys.SanitizeForLog(_bucket), DocumentKeys.SanitizeForLog(key));
        await _client.PutObjectAsync(request, ct);

        var head = await _client.GetObjectMetadataAsync(_bucket, key, ct);
        return new DocumentUploadResult
        {
            Uri       = GetUri(container, blobName),
            Container = container,
            BlobName  = blobName,
            SizeBytes = head.ContentLength
        };
    }

    public async Task<Stream> DownloadAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetObjectAsync(_bucket, DocumentKeys.ObjectName(container, blobName), ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            throw new DocumentNotFoundException(container, blobName);
        }
    }

    public async Task<bool> ExistsAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, DocumentKeys.ObjectName(container, blobName), ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    public async Task DeleteAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        // S3 deletes are idempotent: a missing key is not an error.
        await _client.DeleteObjectAsync(_bucket, DocumentKeys.ObjectName(container, blobName), ct);
    }

    public async Task SetTagsAsync(
        string container, string blobName,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        try
        {
            await _client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = DocumentKeys.ObjectName(container, blobName),
                Tagging = new Tagging { TagSet = ToTagSet(tags) ?? new List<Tag>() }
            }, ct);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            throw new DocumentNotFoundException(container, blobName);
        }
    }

    public async Task<DocumentProperties> GetPropertiesAsync(
        string container, string blobName,
        CancellationToken ct = default)
    {
        var key = DocumentKeys.ObjectName(container, blobName);
        try
        {
            var head = await _client.GetObjectMetadataAsync(_bucket, key, ct);
            var tagging = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = key
            }, ct);

            return new DocumentProperties
            {
                SizeBytes   = head.ContentLength,
                ContentType = head.Headers.ContentType,
                Tags        = (tagging.Tagging ?? new List<Tag>()).ToDictionary(t => t.Key, t => t.Value)
            };
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            throw new DocumentNotFoundException(container, blobName);
        }
    }

    public async Task<DocumentUploadLink?> CreateUploadLinkAsync(
        string container, string blobName, string contentType,
        DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        var url = await _client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = DocumentKeys.ObjectName(container, blobName),
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAtUtc.UtcDateTime,
            // HTTPS everywhere except a plain-HTTP local endpoint (MinIO in tests).
            Protocol = _client.Config.ServiceURL?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                ? Protocol.HTTP
                : Protocol.HTTPS
        });

        return new DocumentUploadLink
        {
            Url = new Uri(url),
            Headers = new Dictionary<string, string> { ["Content-Type"] = contentType },
            ExpiresAtUtc = expiresAtUtc
        };
    }

    public Uri GetUri(string container, string blobName) =>
        new($"s3://{_bucket}/{DocumentKeys.ObjectName(container, blobName)}");

    private static List<Tag>? ToTagSet(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
            return null;
        if (tags.Count > MaxTags)
            throw new ArgumentException($"S3 allows at most {MaxTags} tags per object; got {tags.Count}.", nameof(tags));

        return tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList();
    }

    private static bool IsNotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        || ex.ErrorCode is "NoSuchKey" or "NotFound";

    private static async Task<FileStream> BufferAsync(Stream content, CancellationToken ct)
    {
        var temp = new FileStream(
            Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await content.CopyToAsync(temp, ct);
        temp.Position = 0;
        return temp;
    }
}
