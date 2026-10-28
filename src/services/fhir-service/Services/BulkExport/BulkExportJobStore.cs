using System.Collections.Concurrent;
using FhirService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.BulkExport;

/// <summary>One NDJSON file an export wrote, with where it is stored.</summary>
public sealed record BulkExportStoredFile(string Type, string Name, int Count, string Key);

/// <summary>A bulk export job as the store keeps it.</summary>
public sealed record BulkExportJobRecord
{
    public required string TenantId { get; init; }
    public required string JobId { get; init; }
    public required string RequestedBy { get; init; }

    /// <summary>
    /// The kick-off that started it. A Group export's Group and a Patient
    /// export's "everyone the caller may export" are the same provider panel
    /// today; the level is kept so the two are never confused later.
    /// </summary>
    public BulkExportLevel Level { get; init; } = BulkExportLevel.Group;

    /// <summary>The Group a Group export named; null for a Patient export.</summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// For a Payer-to-Payer export, the requesting payer the client was
    /// registered to when it started the export. Null otherwise.
    /// </summary>
    public string? ReceivingPayerId { get; init; }
    public required IReadOnlyList<string> Types { get; init; }
    public DateTimeOffset? Since { get; init; }
    public required string RequestUrl { get; init; }
    public BulkExportStatus Status { get; init; } = BulkExportStatus.Accepted;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the files are deleted, however the job ended.</summary>
    public DateTimeOffset? PurgeAt { get; init; }
    public bool Purged { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }

    /// <summary>The time the export's data was read as of: the manifest's <c>transactionTime</c>.</summary>
    public DateTimeOffset? TransactionTime { get; init; }
    public int ProgressPercent { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<BulkExportStoredFile> Files { get; init; } = [];

    public BulkExportJob ToJob() => new()
    {
        JobId = JobId,
        Status = Status,
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt,
        Request = RequestUrl,
        Level = Level,
        GroupId = GroupId,
        ReceivingPayerId = ReceivingPayerId,
        ResourceTypes = Types.ToList(),
        Since = Since?.ToString("o"),
        ErrorMessage = Error,
        ProgressPercent = ProgressPercent,
        RequestedBy = RequestedBy,
        Files = Files.Select(f => new BulkExportFile { Type = f.Type, Name = f.Name, Count = f.Count }).ToList(),
        ExpiresAt = ExpiresAt,
        TransactionTime = TransactionTime,
    };
}

/// <summary>
/// Durable bulk export jobs. Every read and write names the tenant; a job is
/// never found from another tenant.
/// </summary>
public interface IBulkExportJobStore
{
    Task CreateAsync(BulkExportJobRecord job, CancellationToken ct = default);

    Task<BulkExportJobRecord?> GetAsync(string tenantId, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Claims the oldest job waiting to run, or one whose worker's lease ran
    /// out, for <paramref name="leaseUntil"/>. Null when there is none.
    /// </summary>
    Task<BulkExportJobRecord?> ClaimNextAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default);

    /// <summary>Records progress and extends the lease. False when the job is no longer running (cancelled).</summary>
    Task<bool> ReportProgressAsync(
        string tenantId, string jobId, int percent, DateTimeOffset leaseUntil, CancellationToken ct = default);

    /// <summary>Marks a running job complete with its files. False when it is no longer running.</summary>
    Task<bool> CompleteAsync(
        string tenantId, string jobId, IReadOnlyList<BulkExportStoredFile> files, DateTimeOffset transactionTime,
        DateTimeOffset completedAt, DateTimeOffset expiresAt, DateTimeOffset purgeAt, CancellationToken ct = default);

    Task FailAsync(string tenantId, string jobId, string error, DateTimeOffset completedAt, DateTimeOffset purgeAt,
        CancellationToken ct = default);

    /// <summary>Cancels a job that has not already been cancelled, and makes its files due for deletion now.</summary>
    Task<bool> CancelAsync(string tenantId, string jobId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Jobs whose files are due for deletion.</summary>
    Task<IReadOnlyList<BulkExportJobRecord>> ListPurgeableAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    Task MarkPurgedAsync(string tenantId, string jobId, CancellationToken ct = default);
}

public sealed class MongoBulkExportJobStore : IBulkExportJobStore
{
    public const string CollectionName = "bulk_export_jobs";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoBulkExportJobStore(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _collection = database.GetCollection<BsonDocument>(CollectionName);
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tenantId").Ascending("jobId"),
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_job" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("status").Ascending("createdAt"),
                new CreateIndexOptions { Name = "ix_status_created" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("purged").Ascending("purgeAt"),
                new CreateIndexOptions { Name = "ix_purged_purge_at" }),
        ]);
    }

    private static FilterDefinitionBuilder<BsonDocument> F => Builders<BsonDocument>.Filter;
    private static UpdateDefinitionBuilder<BsonDocument> U => Builders<BsonDocument>.Update;

    private static FilterDefinition<BsonDocument> Key(string tenantId, string jobId)
        => F.Eq("tenantId", tenantId) & F.Eq("jobId", jobId);

    private static FilterDefinition<BsonDocument> Running(string tenantId, string jobId)
        => Key(tenantId, jobId) & F.Eq("status", BulkExportStatus.InProgress.ToString());

    public Task CreateAsync(BulkExportJobRecord job, CancellationToken ct = default)
        => _collection.InsertOneAsync(ToDocument(job), cancellationToken: ct);

    public async Task<BulkExportJobRecord?> GetAsync(string tenantId, string jobId, CancellationToken ct = default)
    {
        var d = await _collection.Find(Key(tenantId, jobId)).FirstOrDefaultAsync(ct);
        return d is null ? null : FromDocument(d);
    }

    public async Task<BulkExportJobRecord?> ClaimNextAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var waiting = F.Eq("status", BulkExportStatus.Accepted.ToString())
            | (F.Eq("status", BulkExportStatus.InProgress.ToString()) & F.Lt("leaseUntil", now.UtcDateTime));
        var d = await _collection.FindOneAndUpdateAsync(
            waiting,
            U.Set("status", BulkExportStatus.InProgress.ToString()).Set("leaseUntil", leaseUntil.UtcDateTime),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                Sort = Builders<BsonDocument>.Sort.Ascending("createdAt"),
                ReturnDocument = ReturnDocument.After,
            },
            ct);
        return d is null ? null : FromDocument(d);
    }

    public async Task<bool> ReportProgressAsync(
        string tenantId, string jobId, int percent, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var result = await _collection.UpdateOneAsync(
            Running(tenantId, jobId),
            U.Set("progressPercent", percent).Set("leaseUntil", leaseUntil.UtcDateTime),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> CompleteAsync(
        string tenantId, string jobId, IReadOnlyList<BulkExportStoredFile> files, DateTimeOffset transactionTime,
        DateTimeOffset completedAt, DateTimeOffset expiresAt, DateTimeOffset purgeAt, CancellationToken ct = default)
    {
        var result = await _collection.UpdateOneAsync(
            Running(tenantId, jobId),
            U.Set("status", BulkExportStatus.Complete.ToString())
                .Set("files", new BsonArray(files.Select(FileDocument)))
                .Set("transactionTime", transactionTime.UtcDateTime)
                .Set("completedAt", completedAt.UtcDateTime)
                .Set("expiresAt", expiresAt.UtcDateTime)
                .Set("purgeAt", purgeAt.UtcDateTime)
                .Set("progressPercent", 100)
                .Unset("leaseUntil"),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public Task FailAsync(string tenantId, string jobId, string error, DateTimeOffset completedAt, DateTimeOffset purgeAt,
        CancellationToken ct = default)
        => _collection.UpdateOneAsync(
            Running(tenantId, jobId),
            U.Set("status", BulkExportStatus.Error.ToString())
                .Set("error", error)
                .Set("completedAt", completedAt.UtcDateTime)
                .Set("purgeAt", purgeAt.UtcDateTime)
                .Unset("leaseUntil"),
            cancellationToken: ct);

    public async Task<bool> CancelAsync(string tenantId, string jobId, DateTimeOffset now, CancellationToken ct = default)
    {
        var result = await _collection.UpdateOneAsync(
            Key(tenantId, jobId) & F.Ne("status", BulkExportStatus.Cancelled.ToString()),
            U.Set("status", BulkExportStatus.Cancelled.ToString())
                .Set("completedAt", now.UtcDateTime)
                .Set("purgeAt", now.UtcDateTime)
                .Unset("leaseUntil"),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<IReadOnlyList<BulkExportJobRecord>> ListPurgeableAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var docs = await _collection
            .Find(F.Ne("purged", true) & F.Lte("purgeAt", now.UtcDateTime))
            .Limit(limit)
            .ToListAsync(ct);
        return docs.Select(FromDocument).ToList();
    }

    public Task MarkPurgedAsync(string tenantId, string jobId, CancellationToken ct = default)
        => _collection.UpdateOneAsync(Key(tenantId, jobId), U.Set("purged", true).Set("files", new BsonArray()), cancellationToken: ct);

    private static BsonDocument FileDocument(BulkExportStoredFile f) => new()
    {
        ["type"] = f.Type, ["name"] = f.Name, ["count"] = f.Count, ["key"] = f.Key,
    };

    private static BsonValue Date(DateTimeOffset? value) => value is { } v ? v.UtcDateTime : BsonNull.Value;

    private static DateTimeOffset? Date(BsonDocument d, string name)
        => d.TryGetValue(name, out var v) && !v.IsBsonNull ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero) : null;

    private static BsonDocument ToDocument(BulkExportJobRecord j) => new()
    {
        ["tenantId"] = j.TenantId,
        ["jobId"] = j.JobId,
        ["requestedBy"] = j.RequestedBy,
        ["level"] = j.Level.ToString(),
        ["groupId"] = j.GroupId is null ? BsonNull.Value : j.GroupId,
        ["receivingPayerId"] = j.ReceivingPayerId is null ? BsonNull.Value : j.ReceivingPayerId,
        ["types"] = new BsonArray(j.Types),
        ["since"] = Date(j.Since),
        ["requestUrl"] = j.RequestUrl,
        ["status"] = j.Status.ToString(),
        ["createdAt"] = j.CreatedAt.UtcDateTime,
        ["completedAt"] = Date(j.CompletedAt),
        ["expiresAt"] = Date(j.ExpiresAt),
        ["purgeAt"] = Date(j.PurgeAt),
        ["purged"] = j.Purged,
        ["leaseUntil"] = Date(j.LeaseUntil),
        ["transactionTime"] = Date(j.TransactionTime),
        ["progressPercent"] = j.ProgressPercent,
        ["error"] = j.Error is null ? BsonNull.Value : j.Error,
        ["files"] = new BsonArray(j.Files.Select(FileDocument)),
    };

    private static BulkExportJobRecord FromDocument(BsonDocument d) => new()
    {
        TenantId = d["tenantId"].AsString,
        JobId = d["jobId"].AsString,
        RequestedBy = d["requestedBy"].AsString,
        // Jobs written before Patient exports existed are all Group exports.
        Level = d.TryGetValue("level", out var level) && Enum.TryParse<BulkExportLevel>(level.AsString, out var parsed)
            ? parsed
            : BulkExportLevel.Group,
        GroupId = d.TryGetValue("groupId", out var group) && group.IsString ? group.AsString : null,
        ReceivingPayerId = d.TryGetValue("receivingPayerId", out var payer) && payer.IsString ? payer.AsString : null,
        Types = d["types"].AsBsonArray.Select(t => t.AsString).ToList(),
        Since = Date(d, "since"),
        RequestUrl = d["requestUrl"].AsString,
        Status = Enum.Parse<BulkExportStatus>(d["status"].AsString),
        CreatedAt = Date(d, "createdAt")!.Value,
        CompletedAt = Date(d, "completedAt"),
        ExpiresAt = Date(d, "expiresAt"),
        PurgeAt = Date(d, "purgeAt"),
        Purged = d.TryGetValue("purged", out var p) && p.IsBoolean && p.AsBoolean,
        LeaseUntil = Date(d, "leaseUntil"),
        TransactionTime = Date(d, "transactionTime"),
        ProgressPercent = d.TryGetValue("progressPercent", out var pp) && pp.IsInt32 ? pp.AsInt32 : 0,
        Error = d.TryGetValue("error", out var e) && e.IsString ? e.AsString : null,
        Files = d.TryGetValue("files", out var files)
            ? files.AsBsonArray.Select(f => f.AsBsonDocument)
                .Select(f => new BulkExportStoredFile(f["type"].AsString, f["name"].AsString, f["count"].AsInt32, f["key"].AsString))
                .ToList()
            : [],
    };
}

/// <summary>In-process job store for tests and local runs; not durable.</summary>
public sealed class InMemoryBulkExportJobStore : IBulkExportJobStore
{
    private readonly ConcurrentDictionary<(string, string), BulkExportJobRecord> _jobs = new();
    private readonly object _gate = new();

    public IReadOnlyCollection<BulkExportJobRecord> Jobs => _jobs.Values.ToList();

    public Task CreateAsync(BulkExportJobRecord job, CancellationToken ct = default)
    {
        if (!_jobs.TryAdd((job.TenantId, job.JobId), job))
            throw new InvalidOperationException("Duplicate job id.");
        return Task.CompletedTask;
    }

    public Task<BulkExportJobRecord?> GetAsync(string tenantId, string jobId, CancellationToken ct = default)
        => Task.FromResult(_jobs.TryGetValue((tenantId, jobId), out var j) ? j : null);

    public Task<BulkExportJobRecord?> ClaimNextAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var next = _jobs.Values
                .Where(j => j.Status == BulkExportStatus.Accepted
                            || (j.Status == BulkExportStatus.InProgress && j.LeaseUntil < now))
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefault();
            if (next is null) return Task.FromResult<BulkExportJobRecord?>(null);
            var claimed = next with { Status = BulkExportStatus.InProgress, LeaseUntil = leaseUntil };
            _jobs[(next.TenantId, next.JobId)] = claimed;
            return Task.FromResult<BulkExportJobRecord?>(claimed);
        }
    }

    private bool UpdateRunning(string tenantId, string jobId, Func<BulkExportJobRecord, BulkExportJobRecord> update)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((tenantId, jobId), out var j) || j.Status != BulkExportStatus.InProgress)
                return false;
            _jobs[(tenantId, jobId)] = update(j);
            return true;
        }
    }

    public Task<bool> ReportProgressAsync(
        string tenantId, string jobId, int percent, DateTimeOffset leaseUntil, CancellationToken ct = default)
        => Task.FromResult(UpdateRunning(tenantId, jobId, j => j with { ProgressPercent = percent, LeaseUntil = leaseUntil }));

    public Task<bool> CompleteAsync(
        string tenantId, string jobId, IReadOnlyList<BulkExportStoredFile> files, DateTimeOffset transactionTime,
        DateTimeOffset completedAt, DateTimeOffset expiresAt, DateTimeOffset purgeAt, CancellationToken ct = default)
        => Task.FromResult(UpdateRunning(tenantId, jobId, j => j with
        {
            Status = BulkExportStatus.Complete, Files = files, TransactionTime = transactionTime, CompletedAt = completedAt,
            ExpiresAt = expiresAt, PurgeAt = purgeAt, ProgressPercent = 100, LeaseUntil = null,
        }));

    public Task FailAsync(string tenantId, string jobId, string error, DateTimeOffset completedAt, DateTimeOffset purgeAt,
        CancellationToken ct = default)
    {
        UpdateRunning(tenantId, jobId, j => j with
        {
            Status = BulkExportStatus.Error, Error = error, CompletedAt = completedAt, PurgeAt = purgeAt, LeaseUntil = null,
        });
        return Task.CompletedTask;
    }

    public Task<bool> CancelAsync(string tenantId, string jobId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue((tenantId, jobId), out var j) || j.Status == BulkExportStatus.Cancelled)
                return Task.FromResult(false);
            _jobs[(tenantId, jobId)] = j with
            {
                Status = BulkExportStatus.Cancelled, CompletedAt = now, PurgeAt = now, LeaseUntil = null,
            };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<BulkExportJobRecord>> ListPurgeableAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BulkExportJobRecord>>(
            _jobs.Values.Where(j => !j.Purged && j.PurgeAt <= now).Take(limit).ToList());

    public Task MarkPurgedAsync(string tenantId, string jobId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue((tenantId, jobId), out var j))
                _jobs[(tenantId, jobId)] = j with { Purged = true, Files = [] };
        }
        return Task.CompletedTask;
    }
}
