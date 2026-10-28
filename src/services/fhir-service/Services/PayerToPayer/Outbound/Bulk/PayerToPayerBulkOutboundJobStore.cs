using System.Text.Json;
using System.Text.Json.Serialization;
using FhirService.Models.PayerToPayer;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.PayerToPayer.Outbound.Bulk;

/// <summary>
/// Outbound bulk Payer-to-Payer jobs. A job is advanced by whichever worker
/// claims it, so several instances share the work without two advancing the
/// same job at once.
/// </summary>
public interface IPayerToPayerBulkOutboundJobStore
{
    Task SaveAsync(PayerToPayerBulkOutboundJob job, CancellationToken ct = default);

    /// <summary>A job of the tenant, while it has not expired.</summary>
    Task<PayerToPayerBulkOutboundJob?> GetAsync(
        string tenantId, string jobId, DateTimeOffset nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Up to <paramref name="max"/> unfinished jobs due at <paramref name="nowUtc"/>
    /// and not held by another worker, each now held until <paramref name="leaseUntil"/>.
    /// </summary>
    Task<IReadOnlyList<PayerToPayerBulkOutboundJob>> ClaimDueAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseUntil, int max, CancellationToken ct = default);
}

/// <summary>
/// MongoDB store (<c>p2p_outbound_bulk_jobs</c>): the fields jobs are found by
/// are top-level, the job itself is kept as JSON. Finished jobs are removed by
/// a TTL index at <see cref="PayerToPayerBulkOutboundJob.ExpiresAt"/>.
/// Singleton: indexes are ensured once at construction.
/// </summary>
public sealed class MongoPayerToPayerBulkOutboundJobStore : IPayerToPayerBulkOutboundJobStore
{
    public const string CollectionName = "p2p_outbound_bulk_jobs";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoPayerToPayerBulkOutboundJobStore(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _collection = database.GetCollection<BsonDocument>(CollectionName);
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("jobId"),
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_job" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("terminal").Ascending("nextAttemptAt"),
                new CreateIndexOptions { Name = "ix_due" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("expiresAt"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Name = "ttl_expiresAt" }),
        ]);
    }

    public Task SaveAsync(PayerToPayerBulkOutboundJob job, CancellationToken ct = default)
        => _collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("tenantId", job.TenantId) & Builders<BsonDocument>.Filter.Eq("jobId", job.JobId),
            ToDocument(job), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<PayerToPayerBulkOutboundJob?> GetAsync(
        string tenantId, string jobId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
                     & Builders<BsonDocument>.Filter.Eq("jobId", jobId)
                     & Builders<BsonDocument>.Filter.Gt("expiresAt", nowUtc.UtcDateTime);
        var document = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return document is null ? null : FromDocument(document);
    }

    public async Task<IReadOnlyList<PayerToPayerBulkOutboundJob>> ClaimDueAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseUntil, int max, CancellationToken ct = default)
    {
        var f = Builders<BsonDocument>.Filter;
        var filter = f.Eq("terminal", false)
                     & f.Lte("nextAttemptAt", nowUtc.UtcDateTime)
                     & f.Lte("leasedUntil", nowUtc.UtcDateTime);
        var update = Builders<BsonDocument>.Update.Set("leasedUntil", leaseUntil.UtcDateTime);

        var claimed = new List<PayerToPayerBulkOutboundJob>();
        while (claimed.Count < max)
        {
            // One at a time, atomically: a job another worker took in between
            // no longer matches the filter.
            var document = await _collection.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
            if (document is null) break;
            var job = FromDocument(document);
            job.LeasedUntil = leaseUntil;
            claimed.Add(job);
        }
        return claimed;
    }

    private static BsonDocument ToDocument(PayerToPayerBulkOutboundJob job) => new()
    {
        ["tenantId"] = job.TenantId,
        ["jobId"] = job.JobId,
        ["terminal"] = job.IsTerminal,
        ["nextAttemptAt"] = job.NextAttemptAt.UtcDateTime,
        // Never null, so "not held" is just "held until the past".
        ["leasedUntil"] = (job.LeasedUntil ?? DateTimeOffset.UnixEpoch).UtcDateTime,
        ["expiresAt"] = job.ExpiresAt.UtcDateTime,
        ["job"] = PayerToPayerBulkOutboundJobJson.Serialize(job),
    };

    private static PayerToPayerBulkOutboundJob FromDocument(BsonDocument document)
        => PayerToPayerBulkOutboundJobJson.Deserialize(document["job"].AsString);
}

/// <summary>
/// In-memory store, for tests and hosts without MongoDB. Jobs are copied in
/// and out, as a database would, so a caller holding one does not change the
/// stored job by accident.
/// </summary>
public sealed class InMemoryPayerToPayerBulkOutboundJobStore : IPayerToPayerBulkOutboundJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Job), string> _jobs = new();

    public Task SaveAsync(PayerToPayerBulkOutboundJob job, CancellationToken ct = default)
    {
        lock (_gate) _jobs[(job.TenantId, job.JobId)] = PayerToPayerBulkOutboundJobJson.Serialize(job);
        return Task.CompletedTask;
    }

    public Task<PayerToPayerBulkOutboundJob?> GetAsync(
        string tenantId, string jobId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((tenantId, jobId), out var json)) return Task.FromResult<PayerToPayerBulkOutboundJob?>(null);
            var job = PayerToPayerBulkOutboundJobJson.Deserialize(json);
            return Task.FromResult(job.ExpiresAt > nowUtc ? job : null);
        }
    }

    public Task<IReadOnlyList<PayerToPayerBulkOutboundJob>> ClaimDueAsync(
        DateTimeOffset nowUtc, DateTimeOffset leaseUntil, int max, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var claimed = new List<PayerToPayerBulkOutboundJob>();
            foreach (var (key, json) in _jobs.ToList())
            {
                if (claimed.Count >= max) break;
                var job = PayerToPayerBulkOutboundJobJson.Deserialize(json);
                if (job.IsTerminal || job.NextAttemptAt > nowUtc || (job.LeasedUntil ?? DateTimeOffset.MinValue) > nowUtc)
                    continue;
                job.LeasedUntil = leaseUntil;
                _jobs[key] = PayerToPayerBulkOutboundJobJson.Serialize(job);
                claimed.Add(job);
            }
            return Task.FromResult<IReadOnlyList<PayerToPayerBulkOutboundJob>>(claimed);
        }
    }
}

internal static class PayerToPayerBulkOutboundJobJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(PayerToPayerBulkOutboundJob job) => JsonSerializer.Serialize(job, Options);

    public static PayerToPayerBulkOutboundJob Deserialize(string json)
        => JsonSerializer.Deserialize<PayerToPayerBulkOutboundJob>(json, Options)!;
}
