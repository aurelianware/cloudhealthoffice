using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CloudHealthOffice.DocumentStore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace EnrollmentImportService.Services.Runs;

/// <summary>
/// Where raw inbound EDI files are kept: staged on receipt, read back by the
/// import run, and kept as the raw-file archive (edi-operations.md §10–11).
/// Keys are tenant-scoped (<c>tenants/{tenant}/edi/inbound/{type}/{yyyy}/{MM}/{dd}/{id}</c>)
/// and carry no file name, which may name a sponsor.
/// </summary>
public interface IRawEdiFileStore
{
    /// <summary>Document store, GridFs or InMemory: recorded on the run.</summary>
    string Kind { get; }

    /// <summary>Stores the stream as read (not buffered in memory) and returns its key.</summary>
    Task<string> SaveAsync(string tenantId, string transactionType, Stream content, CancellationToken ct = default);

    /// <summary>Opens a stored file for reading. Throws when the key is not this tenant's or the file is gone.</summary>
    Task<Stream> OpenAsync(string tenantId, string key, CancellationToken ct = default);
}

public static partial class RawEdiFileKeys
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex Segment();

    public static string New(string tenantId, string transactionType, DateTime now)
    {
        if (!Segment().IsMatch(tenantId) || tenantId is "." or "..")
            throw new ArgumentException("The tenant id cannot be used in a storage key.", nameof(tenantId));
        var type = Segment().IsMatch(transactionType) ? transactionType : "x12";
        return $"tenants/{tenantId}/edi/inbound/{type}/{now:yyyy}/{now:MM}/{now:dd}/{Guid.NewGuid():N}";
    }

    public static void EnsureTenant(string tenantId, string key)
    {
        if (!key.StartsWith($"tenants/{tenantId}/", StringComparison.Ordinal) || key.Contains(".."))
            throw new UnauthorizedAccessException("The raw file does not belong to this tenant.");
    }
}

/// <summary>Raw files in the configured document store (Azure Blob, Google Cloud Storage or S3).</summary>
public sealed class DocumentStoreRawEdiFileStore(IDocumentStore store, TimeProvider clock) : IRawEdiFileStore
{
    public const string Container = "edi-raw-files";

    private static readonly IReadOnlyDictionary<string, string> Tags = new Dictionary<string, string>
    {
        // edi-operations.md §10 decision 1: 10 years by default.
        ["retention-policy"] = "edi-raw-10y",
    };

    public string Kind => "DocumentStore";

    public async Task<string> SaveAsync(string tenantId, string transactionType, Stream content, CancellationToken ct = default)
    {
        var key = RawEdiFileKeys.New(tenantId, transactionType, clock.GetUtcNow().UtcDateTime);
        await store.UploadAsync(Container, key, content, "application/edi-x12", Tags, ct);
        return key;
    }

    public Task<Stream> OpenAsync(string tenantId, string key, CancellationToken ct = default)
    {
        RawEdiFileKeys.EnsureTenant(tenantId, key);
        return store.DownloadAsync(Container, key, ct);
    }
}

/// <summary>
/// Raw files in MongoDB GridFS, for deployments without a document store
/// configured: chunked, streamed both ways, and shared by every replica so a
/// run resumes on another pod.
/// </summary>
public sealed class GridFsRawEdiFileStore(IMongoDatabase database, TimeProvider clock) : IRawEdiFileStore
{
    public const string BucketName = "edi-raw-files";

    private readonly GridFSBucket _bucket = new(database, new GridFSBucketOptions { BucketName = BucketName, ChunkSizeBytes = 255 * 1024 });

    public string Kind => "GridFs";

    public async Task<string> SaveAsync(string tenantId, string transactionType, Stream content, CancellationToken ct = default)
    {
        var key = RawEdiFileKeys.New(tenantId, transactionType, clock.GetUtcNow().UtcDateTime);
        await _bucket.UploadFromStreamAsync(key, content, new GridFSUploadOptions
        {
            Metadata = new BsonDocument { ["tenantId"] = tenantId, ["retentionPolicy"] = "edi-raw-10y" },
        }, ct);
        return key;
    }

    public async Task<Stream> OpenAsync(string tenantId, string key, CancellationToken ct = default)
    {
        RawEdiFileKeys.EnsureTenant(tenantId, key);
        var filter = Builders<GridFSFileInfo>.Filter.And(
            Builders<GridFSFileInfo>.Filter.Eq(f => f.Filename, key),
            Builders<GridFSFileInfo>.Filter.Eq("metadata.tenantId", tenantId));
        using var cursor = await _bucket.FindAsync(filter, cancellationToken: ct);
        var file = await cursor.FirstOrDefaultAsync(ct)
                   ?? throw new FileNotFoundException("The raw file is not stored.", key);
        return await _bucket.OpenDownloadStreamAsync(file.Id, new GridFSDownloadOptions { Seekable = false }, ct);
    }
}

/// <summary>Tests and local development only.</summary>
public sealed class InMemoryRawEdiFileStore(TimeProvider? clock = null) : IRawEdiFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public string Kind => "InMemory";

    public int Count => _files.Count;

    public async Task<string> SaveAsync(string tenantId, string transactionType, Stream content, CancellationToken ct = default)
    {
        var key = RawEdiFileKeys.New(tenantId, transactionType, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        _files[key] = copy.ToArray();
        return key;
    }

    public Task<Stream> OpenAsync(string tenantId, string key, CancellationToken ct = default)
    {
        RawEdiFileKeys.EnsureTenant(tenantId, key);
        return _files.TryGetValue(key, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException("The raw file is not stored.", key);
    }
}
