using CloudHealthOffice.Infrastructure.Data;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// MongoDB-backed <see cref="IBatchJobContainer"/>: the job-document store
/// behind <see cref="BatchJobStore"/> (self-hosted MongoDB or Azure Cosmos DB
/// for MongoDB vCore).
///
/// Two tenants may use the same job id. MongoDB's <c>_id</c> must be unique
/// across the whole collection, so this adapter composes <c>_id</c> as
/// <c>"{tenantId}::{id}"</c>: a job id collision across tenants stores as two
/// separate documents.
///
/// TTL: completed jobs expire through a TTL index on <c>expiresAt</c> — call
/// <see cref="EnsureIndexesAsync"/> once at startup.
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

    /// <summary>
    /// Synchronous <see cref="EnsureIndexesAsync"/> for registration-time callers (a DI
    /// factory cannot await): uses the driver's own sync <c>CreateOne</c> rather than
    /// blocking on the async call.
    /// </summary>
    public void EnsureIndexes()
    {
        var indexKeys = Builders<JobDoc>.IndexKeys.Ascending(d => d.ExpiresAt);
        var indexOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.Zero };
        _collection.Indexes.CreateOne(new CreateIndexModel<JobDoc>(indexKeys, indexOptions));
    }

    // Each write $sets only the part of the job document it owns (the job, or
    // one payload slot) instead of reading the whole document and replacing it:
    // a replace from a separate read erased whatever another writer stored in
    // between (a job save dropping the result payload, a payload write
    // reverting the job's status). Upserts create the document as before.

    public async Task UpsertAsync(BatchEligibilityJob job, string partitionKey, CancellationToken ct)
    {
        var docId = ComposeId(job.Id, partitionKey);
        var update = Builders<JobDoc>.Update
            .Set(d => d.Job, job)
            .SetOnInsert(d => d.TenantId, partitionKey);
        await _collection.UpdateOneAsync(
            d => d.Id == docId, update, new UpdateOptions { IsUpsert = true }, ct);
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
        var update = Builders<JobDoc>.Update
            .Set(d => d.Payloads[payloadKey], new PayloadSlot { Inline = Convert.ToBase64String(bytes) })
            .SetOnInsert(d => d.TenantId, partitionKey);
        await _collection.UpdateOneAsync(
            d => d.Id == docId, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task RecordBlobPayloadAsync(
        string id, string partitionKey, string payloadKey, string blobUri, CancellationToken ct)
    {
        var docId = ComposeId(id, partitionKey);
        var update = Builders<JobDoc>.Update
            .Set(d => d.Payloads[payloadKey], new PayloadSlot { BlobUri = blobUri })
            .Set(d => d.Job.StorageMode, BatchStorageMode.Blob)
            .SetOnInsert(d => d.TenantId, partitionKey);
        if (payloadKey == "input") update = update.Set(d => d.Job.InputBlobUri, blobUri);
        if (payloadKey == "result") update = update.Set(d => d.Job.ResultBlobUri, blobUri);
        await _collection.UpdateOneAsync(
            d => d.Id == docId, update, new UpdateOptions { IsUpsert = true }, ct);
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

    [CrossTenantQuery("Read by document id, and every id is composed as {tenantId}::{id} by BatchJobStore from the request's tenant.")]
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
