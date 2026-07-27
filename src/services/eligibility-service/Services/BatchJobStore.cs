using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using EligibilityService.Models;
using Microsoft.Azure.Cosmos;

namespace EligibilityService.Services;

/// <summary>
/// Persistent IBatchJobStore implementation backed by a pluggable document
/// store (Cosmos DB or MongoDB, via IBatchJobContainer) + Azure Blob
/// Storage (large payloads).
///
/// Small payloads (&lt; <see cref="InlineMaxBytes"/>, default 1 MB) are
/// embedded on the job doc as base64. Larger payloads are written to a blob
/// container addressed by <c>{tenantId}/{jobId}/{kind}.csv</c> and the job
/// doc records the blob URI and <see cref="BatchStorageMode.Blob"/>. Reads
/// route back through this store so we never hand out SAS URIs.
///
/// Partitioned/scoped by tenantId regardless of backend. Cosmos mode
/// carries a <c>defaultTtl</c>-inherited TTL (set at container
/// provisioning) so completed jobs expire automatically; the matching
/// blob-lifecycle rule is provisioned in
/// scripts/azure/provision-batch-eligibility.sh. Mongo mode relies on a
/// TTL index instead (see MongoContainerAdapter).
/// </summary>
public class BatchJobStore : IBatchJobStore
{
    public const int DefaultInlineMaxBytes = 1_048_576;

    private readonly IBatchJobContainer _container;
    private readonly IBatchBlobContainer _blobs;
    private readonly int _inlineMaxBytes;

    public BatchJobStore(
        IBatchJobContainer container,
        IBatchBlobContainer blobs,
        int inlineMaxBytes = DefaultInlineMaxBytes)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _inlineMaxBytes = inlineMaxBytes;
    }

    // ── IBatchJobStore: job doc CRUD ─────────────────────────────────────

    public Task SaveAsync(BatchEligibilityJob job, CancellationToken ct = default)
        => _container.UpsertAsync(job, job.TenantId, ct);

    public Task<BatchEligibilityJob?> GetAsync(string tenantId, string jobId, CancellationToken ct = default)
        => _container.ReadAsync(jobId, tenantId, ct);

    // ── IBatchJobStore: payload CRUD ─────────────────────────────────────

    public async Task SaveResultAsync(
        string tenantId, string jobId, byte[] payload, CancellationToken ct = default)
    {
        if (payload.Length < _inlineMaxBytes)
        {
            await _container.WriteInlinePayloadAsync(jobId, tenantId, PayloadKey(jobId), payload, ct);
            return;
        }

        var path = BlobPath(tenantId, jobId);
        using var ms = new MemoryStream(payload, writable: false);
        var uri = await _blobs.UploadAsync(path, ms, ct);
        await _container.RecordBlobPayloadAsync(jobId, tenantId, PayloadKey(jobId), uri.ToString(), ct);
    }

    public async Task<byte[]?> GetResultAsync(
        string tenantId, string jobId, CancellationToken ct = default)
    {
        var record = await _container.ReadPayloadAsync(jobId, tenantId, PayloadKey(jobId), ct);
        if (record == null) return null;
        if (record.Inline != null) return record.Inline;

        using var ms = new MemoryStream();
        await _blobs.DownloadToAsync(BlobPath(tenantId, jobId), ms, ct);
        return ms.ToArray();
    }

    public async Task SaveResultStreamAsync(
        string tenantId, string jobId, Stream source, CancellationToken ct = default)
    {
        // We can't know the length in advance for a forward-only stream,
        // so always route streaming writes to blob. Callers who care about
        // the inline fast path use SaveResultAsync(byte[]) instead.
        var uri = await _blobs.UploadAsync(BlobPath(tenantId, jobId), source, ct);
        await _container.RecordBlobPayloadAsync(jobId, tenantId, PayloadKey(jobId), uri.ToString(), ct);
    }

    public async Task<Stream?> OpenResultStreamAsync(
        string tenantId, string jobId, CancellationToken ct = default)
    {
        var record = await _container.ReadPayloadAsync(jobId, tenantId, PayloadKey(jobId), ct);
        if (record == null) return null;
        if (record.Inline != null) return new MemoryStream(record.Inline, writable: false);
        return await _blobs.OpenReadAsync(BlobPath(tenantId, jobId), ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    // Kept in sync with BatchEligibilityService.InputKey so input vs result
    // route to distinct blob paths.
    private static string BlobPath(string tenantId, string jobId)
    {
        var kind = jobId.StartsWith("INPUT::", StringComparison.Ordinal) ? "input" : "result";
        var realJobId = jobId.StartsWith("INPUT::", StringComparison.Ordinal)
            ? jobId.Substring("INPUT::".Length)
            : jobId;
        return $"{tenantId}/{realJobId}/{kind}.csv";
    }

    private static string PayloadKey(string jobId)
        => jobId.StartsWith("INPUT::", StringComparison.Ordinal) ? "input" : "result";
}

// ─────────────────────────────────────────────────────────────────────────
// Thin wrappers that isolate BatchJobStore from the raw Cosmos SDK / MongoDB driver /
// Blob SDK so the class is fully unit-testable without the emulator.
// The concrete implementations below are trivial adapters; tests swap in
// in-memory fakes.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>Record type returned by <see cref="IBatchJobContainer.ReadPayloadAsync"/>.</summary>
public record BatchPayloadRecord(byte[]? Inline, string? BlobUri);

public interface IBatchJobContainer
{
    Task UpsertAsync(BatchEligibilityJob job, string partitionKey, CancellationToken ct);
    Task<BatchEligibilityJob?> ReadAsync(string id, string partitionKey, CancellationToken ct);

    /// <summary>Writes an inline payload (base64 on the payload sub-document).</summary>
    Task WriteInlinePayloadAsync(
        string id, string partitionKey, string payloadKey, byte[] bytes, CancellationToken ct);

    /// <summary>Records a blob URI for a payload that was written to blob storage.</summary>
    Task RecordBlobPayloadAsync(
        string id, string partitionKey, string payloadKey, string blobUri, CancellationToken ct);

    Task<BatchPayloadRecord?> ReadPayloadAsync(
        string id, string partitionKey, string payloadKey, CancellationToken ct);
}

public interface IBatchBlobContainer
{
    Task<Uri> UploadAsync(string path, Stream content, CancellationToken ct);
    Task DownloadToAsync(string path, Stream destination, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
}

/// <summary>
/// Production adapter over the Cosmos SDK. Stores the job doc itself and a
/// side-car "payloads" dictionary on the doc for inline payloads or blob
/// URIs. This keeps everything in a single document and a single partition
/// read per lookup.
/// </summary>
public class CosmosContainerAdapter : IBatchJobContainer
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public CosmosContainerAdapter(Container container) { _container = container; }

    private record PayloadSlot(string? Inline, string? BlobUri);

    private class JobDoc
    {
        public BatchEligibilityJob Job { get; set; } = new();
        public Dictionary<string, PayloadSlot> Payloads { get; set; } = new();
    }

    public async Task UpsertAsync(BatchEligibilityJob job, string partitionKey, CancellationToken ct)
    {
        var existing = await ReadDocAsync(job.Id, partitionKey, ct);
        var doc = existing ?? new JobDoc();
        doc.Job = job;
        await _container.UpsertItemAsync(WrapAsCosmos(doc, job.Id, partitionKey),
            new PartitionKey(partitionKey), cancellationToken: ct);
    }

    public async Task<BatchEligibilityJob?> ReadAsync(string id, string partitionKey, CancellationToken ct)
    {
        var doc = await ReadDocAsync(id, partitionKey, ct);
        return doc?.Job;
    }

    public async Task WriteInlinePayloadAsync(
        string id, string partitionKey, string payloadKey, byte[] bytes, CancellationToken ct)
    {
        var doc = (await ReadDocAsync(id, partitionKey, ct)) ?? new JobDoc();
        doc.Payloads[payloadKey] = new PayloadSlot(Convert.ToBase64String(bytes), null);
        await _container.UpsertItemAsync(WrapAsCosmos(doc, id, partitionKey),
            new PartitionKey(partitionKey), cancellationToken: ct);
    }

    public async Task RecordBlobPayloadAsync(
        string id, string partitionKey, string payloadKey, string blobUri, CancellationToken ct)
    {
        var doc = (await ReadDocAsync(id, partitionKey, ct)) ?? new JobDoc();
        doc.Payloads[payloadKey] = new PayloadSlot(null, blobUri);
        if (doc.Job != null)
        {
            doc.Job.StorageMode = BatchStorageMode.Blob;
            if (payloadKey == "input") doc.Job.InputBlobUri = blobUri;
            if (payloadKey == "result") doc.Job.ResultBlobUri = blobUri;
        }
        await _container.UpsertItemAsync(WrapAsCosmos(doc, id, partitionKey),
            new PartitionKey(partitionKey), cancellationToken: ct);
    }

    public async Task<BatchPayloadRecord?> ReadPayloadAsync(
        string id, string partitionKey, string payloadKey, CancellationToken ct)
    {
        var doc = await ReadDocAsync(id, partitionKey, ct);
        if (doc == null || !doc.Payloads.TryGetValue(payloadKey, out var slot)) return null;
        return new BatchPayloadRecord(
            Inline: slot.Inline == null ? null : Convert.FromBase64String(slot.Inline),
            BlobUri: slot.BlobUri);
    }

    private async Task<JobDoc?> ReadDocAsync(string id, string partitionKey, CancellationToken ct)
    {
        try
        {
            var response = await _container.ReadItemAsync<Dictionary<string, object>>(
                id, new PartitionKey(partitionKey), cancellationToken: ct);
            var raw = JsonSerializer.Serialize(response.Resource, JsonOpts);
            return JsonSerializer.Deserialize<JobDoc>(raw, JsonOpts);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> WrapAsCosmos(JobDoc doc, string id, string tenantId)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["tenantId"] = tenantId,
            ["job"] = doc.Job,
            ["payloads"] = doc.Payloads
        };
    }
}

public class BlobContainerAdapter : IBatchBlobContainer
{
    private readonly BlobContainerClient _container;

    public BlobContainerAdapter(BlobContainerClient container) { _container = container; }

    public async Task<Uri> UploadAsync(string path, Stream content, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(path);
        await blob.UploadAsync(content, overwrite: true, cancellationToken: ct);
        return blob.Uri;
    }

    public async Task DownloadToAsync(string path, Stream destination, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(path);
        await blob.DownloadToAsync(destination, ct);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(path);
        return await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct);
    }
}
