using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ClaimsService.Import.Cpcds.Drop;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CpcdsDropRunStatus { Running, Succeeded, CompletedWithErrors, Failed }

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CpcdsDropStepStatus { Succeeded, CompletedWithErrors, Failed }

/// <summary>One load of a tenant's drop: the files it took and what each step did.</summary>
[BsonIgnoreExtraElements]
public sealed class CpcdsDropRun
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public CpcdsDropRunStatus Status { get; set; } = CpcdsDropRunStatus.Running;

    /// <summary>When the newest file in the run was written: the extract's age, for the freshness report.</summary>
    public DateTime? NewestFileWrittenAt { get; set; }

    public List<CpcdsDropRunFile> Files { get; set; } = [];
    public List<CpcdsDropStep> Steps { get; set; } = [];

    /// <summary>Where the files went after the run: <c>processed/{runId}</c> or <c>failed/{runId}</c>.</summary>
    public string? ArchivedTo { get; set; }
    /// <summary>Files left waiting, and what ran after the load (e.g. a shadow adjudication run).</summary>
    public List<string> Notes { get; set; } = [];
}

public sealed class CpcdsDropRunFile
{
    public string Name { get; set; } = string.Empty;
    public CpcdsDropFileKind Kind { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

/// <summary>
/// One import a run made: members and coverage, the providers a claim file
/// names, or the claims themselves. <see cref="Counts"/> holds the import's
/// own report numbers (created, updated, rejected ...).
/// </summary>
public sealed class CpcdsDropStep
{
    public string Name { get; set; } = string.Empty;
    public List<string> Files { get; set; } = [];
    public CpcdsDropStepStatus Status { get; set; }
    public int? HttpStatus { get; set; }

    /// <summary>The claim import's batch id: what shadow adjudication and the import transactions are keyed by.</summary>
    public string? ImportBatchId { get; set; }
    public Dictionary<string, decimal> Counts { get; set; } = new(StringComparer.Ordinal);
    public List<string> Errors { get; set; } = [];
}

public interface ICpcdsDropRunRepository
{
    Task SaveAsync(CpcdsDropRun run, CancellationToken ct = default);
    Task<IReadOnlyList<CpcdsDropRun>> ListAsync(string tenantId, int limit, CancellationToken ct = default);
    Task<CpcdsDropRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default);
}

public sealed class CpcdsDropRunRepositoryMongo : ICpcdsDropRunRepository
{
    public const string CollectionName = "CpcdsDropRuns";
    private readonly IMongoCollection<CpcdsDropRun> _runs;

    public CpcdsDropRunRepositoryMongo(IMongoDatabase database)
        => _runs = database.GetCollection<CpcdsDropRun>(CollectionName);

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default)
        => database.GetCollection<CpcdsDropRun>(CollectionName).Indexes.CreateOne(
            new CreateIndexModel<CpcdsDropRun>(
                Builders<CpcdsDropRun>.IndexKeys.Ascending(r => r.TenantId).Descending(r => r.StartedAt),
                new CreateIndexOptions { Name = "tenant_started_desc" }),
            cancellationToken: ct);

    public Task SaveAsync(CpcdsDropRun run, CancellationToken ct = default)
        => _runs.ReplaceOneAsync(r => r.Id == run.Id && r.TenantId == run.TenantId, run,
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<CpcdsDropRun>> ListAsync(string tenantId, int limit, CancellationToken ct = default)
        => await _runs.Find(r => r.TenantId == tenantId)
            .SortByDescending(r => r.StartedAt).Limit(limit).ToListAsync(ct);

    public async Task<CpcdsDropRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default)
        => await _runs.Find(r => r.TenantId == tenantId && r.Id == runId).FirstOrDefaultAsync(ct);
}

public sealed class InMemoryCpcdsDropRunRepository : ICpcdsDropRunRepository
{
    private readonly Dictionary<(string, string), CpcdsDropRun> _runs = [];
    private readonly object _sync = new();

    public Task SaveAsync(CpcdsDropRun run, CancellationToken ct = default)
    {
        lock (_sync) _runs[(run.TenantId, run.Id)] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CpcdsDropRun>> ListAsync(string tenantId, int limit, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<CpcdsDropRun>>(_runs.Values
                .Where(r => r.TenantId == tenantId).OrderByDescending(r => r.StartedAt).Take(limit).ToList());
    }

    public Task<CpcdsDropRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(_runs.GetValueOrDefault((tenantId, runId)));
    }
}
