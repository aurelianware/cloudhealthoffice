using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// MongoDB-backed <see cref="IBatchJobContainer"/> — the portable counterpart
/// to <see cref="CosmosContainerAdapter"/>, so <see cref="BatchJobStore"/> can
/// run against self-hosted MongoDB or Cosmos DB for MongoDB API, not just
/// Cosmos DB for NoSQL (Core/SQL API). Mirrors this service's existing
/// Mongo/Cosmos dual-backend pattern (see EligibilityRepositoryMongo).
///
/// Cosmos containers partition by <c>/tenantId</c>, letting two jobs share an
/// id as long as their partition key differs. MongoDB's <c>_id</c> has no
/// equivalent per-partition scoping — it must be unique across the whole
/// collection — so this adapter composes <c>_id</c> as
/// <c>"{tenantId}::{id}"</c> to preserve the same isolation semantics
/// (a job id collision across tenants stores as two separate documents,
/// exactly as the Cosmos partition key does).
///
/// TTL: Cosmos mode relies on container-level <c>defaultTtl</c> (see
/// scripts/azure/provision-batch-eligibility.sh). Mongo mode needs an
/// explicit TTL index on <c>expiresAt</c> instead — call
/// <see cref="EnsureIndexesAsync"/> once at startup when this adapter is
/// selected.
/// </summary>
public class MongoContainerAdapter : IBatchJobContainer
{
    private readonly IMongoCollection<JobDoc> _collection;

    public MongoContainerAdapter(IMongoCollection<JobDoc> collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>Creates the TTL index on <c>expiresAt</c> if it doesn't already exist. Idempotent.</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var indexKeys = Builders<JobDoc>.IndexKeys.Ascending(d => d.ExpiresAt);
        var indexOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.Zero };
        await _collection.Indexes.CreateOneAsync(
            new CreateIndexModel<JobDoc>(indexKeys, indexOptions), cancellationToken: ct);
    }

    public async Task UpsertAsync(BatchEligibilityJob job, string partitionKey, CancellationToken ct)
    {
        var docId = ComposeId(job.Id, partitionKey);
        var existing = await ReadDocAsync(docId, ct);
        var doc = existing ?? new JobDoc { Id = docId, TenantId = partitionKey };
        doc.Job = job;
        await _collection.ReplaceOneAsync(
            d => d.Id == docId, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task<BatchEligibilityJob?> ReadAsync(string id, string partitionKey, CancellationToken ct)
    {
        var doc = await ReadDocAsync(ComposeId(id, partitionKey), ct);
        return doc?.Job;
    }

    public async Task WriteInlinePayloadAsync(
        string id, string partitionKey, string payloadKey, byte[] bytes, CancellationToken ct)
    {
        var docId = ComposeId(id, partitionKey);
        var doc = (await ReadDocAsync(docId, ct)) ?? new JobDoc { Id = docId, TenantId = partitionKey };
        doc.Payloads[payloadKey] = new PayloadSlot { Inline = Convert.ToBase64String(bytes) };
        await _collection.ReplaceOneAsync(
            d => d.Id == docId, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task RecordBlobPayloadAsync(
        string id, string partitionKey, string payloadKey, string blobUri, CancellationToken ct)
    {
        var docId = ComposeId(id, partitionKey);
        var doc = (await ReadDocAsync(docId, ct)) ?? new JobDoc { Id = docId, TenantId = partitionKey };
        doc.Payloads[payloadKey] = new PayloadSlot { BlobUri = blobUri };
        if (doc.Job != null)
        {
            doc.Job.StorageMode = BatchStorageMode.Blob;
            if (payloadKey == "input") doc.Job.InputBlobUri = blobUri;
            if (payloadKey == "result") doc.Job.ResultBlobUri = blobUri;
        }
        await _collection.ReplaceOneAsync(
            d => d.Id == docId, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task<BatchPayloadRecord?> ReadPayloadAsync(
        string id, string partitionKey, string payloadKey, CancellationToken ct)
    {
        var doc = await ReadDocAsync(ComposeId(id, partitionKey), ct);
        if (doc == null || !doc.Payloads.TryGetValue(payloadKey, out var slot)) return null;
        return new BatchPayloadRecord(
            Inline: slot.Inline == null ? null : Convert.FromBase64String(slot.Inline),
            BlobUri: slot.BlobUri);
    }

    private async Task<JobDoc?> ReadDocAsync(string docId, CancellationToken ct)
    {
        return await _collection.Find(d => d.Id == docId).FirstOrDefaultAsync(ct);
    }

    private static string ComposeId(string id, string tenantId) => $"{tenantId}::{id}";

    public class JobDoc
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public BatchEligibilityJob Job { get; set; } = new();
        public Dictionary<string, PayloadSlot> Payloads { get; set; } = new();

        /// <summary>Backs the TTL index. Set alongside <see cref="Job"/> by callers that know the job's completion/expiry policy.</summary>
        public DateTime? ExpiresAt { get; set; }
    }

    public class PayloadSlot
    {
        public string? Inline { get; set; }
        public string? BlobUri { get; set; }
    }
}
