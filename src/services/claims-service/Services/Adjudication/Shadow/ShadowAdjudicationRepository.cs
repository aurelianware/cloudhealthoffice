using CloudHealthOffice.Infrastructure.Data;
using MongoDB.Driver;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Shadow adjudication runs and their per-claim comparisons. Separate
/// collections from the claims, so a shadow run can never change what a
/// claim, its EOB or a FHIR API shows.
/// </summary>
public interface IShadowAdjudicationRepository
{
    Task SaveRunAsync(ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default);

    Task<IReadOnlyList<ShadowAdjudicationRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default);

    Task<ShadowAdjudicationRun?> GetRunAsync(string tenantId, string runId, CancellationToken ct = default);

    /// <summary>A run's results, optionally only one <see cref="ShadowDiscrepancyKind"/>.</summary>
    Task<IReadOnlyList<ShadowAdjudicationResult>> ListResultsAsync(
        string tenantId, string runId, string? kind, int limit, CancellationToken ct = default);

    /// <summary>Creates or replaces the run record alone (a background run's progress).</summary>
    Task SaveRunStateAsync(ShadowAdjudicationRun run, CancellationToken ct = default);

    /// <summary>Adds one page of a background run's results.</summary>
    Task AppendResultsAsync(IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default);

    /// <summary>The claim ids a run already has results for (to resume a background run).</summary>
    Task<IReadOnlySet<string>> ListResultClaimIdsAsync(string tenantId, string runId, CancellationToken ct = default);

    /// <summary>
    /// Takes the oldest queued background run, or a running one whose lease
    /// has passed, for this replica: marks it Running with a lease until
    /// <paramref name="leaseUntilUtc"/>. Atomic, so two replicas never take the same run.
    /// </summary>
    Task<ShadowAdjudicationRun?> TakeNextBackgroundRunAsync(DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct = default);
}

public sealed class ShadowAdjudicationRepositoryMongo : IShadowAdjudicationRepository
{
    public const string RunsCollectionName = "ShadowAdjudicationRuns";
    public const string ResultsCollectionName = "ShadowAdjudicationResults";
    private const int InsertBatchSize = 100;

    private readonly IMongoCollection<ShadowAdjudicationRun> _runs;
    private readonly IMongoCollection<ShadowAdjudicationResult> _results;

    public ShadowAdjudicationRepositoryMongo(IMongoDatabase database)
    {
        _runs = database.GetCollection<ShadowAdjudicationRun>(RunsCollectionName);
        _results = database.GetCollection<ShadowAdjudicationResult>(ResultsCollectionName);
    }

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default)
    {
        database.GetCollection<ShadowAdjudicationRun>(RunsCollectionName).Indexes.CreateOne(
            new CreateIndexModel<ShadowAdjudicationRun>(
                Builders<ShadowAdjudicationRun>.IndexKeys.Ascending(r => r.TenantId).Descending(r => r.StartedAtUtc),
                new CreateIndexOptions { Name = "tenant_started_desc" }),
            cancellationToken: ct);
        database.GetCollection<ShadowAdjudicationResult>(ResultsCollectionName).Indexes.CreateOne(
            new CreateIndexModel<ShadowAdjudicationResult>(
                Builders<ShadowAdjudicationResult>.IndexKeys
                    .Ascending(r => r.TenantId).Ascending(r => r.RunId).Ascending(r => r.Kind),
                new CreateIndexOptions { Name = "tenant_run_kind" }),
            cancellationToken: ct);
        database.GetCollection<ShadowAdjudicationRun>(RunsCollectionName).Indexes.CreateOne(
            new CreateIndexModel<ShadowAdjudicationRun>(
                Builders<ShadowAdjudicationRun>.IndexKeys.Ascending(r => r.Status).Ascending(r => r.StartedAtUtc),
                new CreateIndexOptions<ShadowAdjudicationRun>
                {
                    Name = "background_status_started",
                    PartialFilterExpression = Builders<ShadowAdjudicationRun>.Filter.Eq(r => r.Background, true),
                }),
            cancellationToken: ct);
    }

    public async Task SaveRunAsync(
        ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default)
    {
        foreach (var batch in results.Chunk(InsertBatchSize))
            await _results.InsertManyAsync(batch, cancellationToken: ct);

        await _runs.ReplaceOneAsync(
            Builders<ShadowAdjudicationRun>.Filter.Eq(r => r.TenantId, run.TenantId)
            & Builders<ShadowAdjudicationRun>.Filter.Eq(r => r.Id, run.Id),
            run,
            new ReplaceOptions { IsUpsert = true },
            ct);
    }

    public Task SaveRunStateAsync(ShadowAdjudicationRun run, CancellationToken ct = default) =>
        _runs.ReplaceOneAsync(
            Builders<ShadowAdjudicationRun>.Filter.Eq(r => r.TenantId, run.TenantId)
            & Builders<ShadowAdjudicationRun>.Filter.Eq(r => r.Id, run.Id),
            run,
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task AppendResultsAsync(IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default)
    {
        foreach (var batch in results.Chunk(InsertBatchSize))
            await _results.InsertManyAsync(batch, cancellationToken: ct);
    }

    public async Task<IReadOnlySet<string>> ListResultClaimIdsAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        var ids = await _results.Find(r => r.TenantId == tenantId && r.RunId == runId)
            .Project(r => r.ClaimId)
            .ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    [CrossTenantQuery("The background shadow worker runs outside any request and takes the next queued run of any tenant; it then works the run under that run's TenantId.")]
    public Task<ShadowAdjudicationRun?> TakeNextBackgroundRunAsync(DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct = default)
    {
        var f = Builders<ShadowAdjudicationRun>.Filter;
        var filter = f.Eq(r => r.Background, true)
                     & (f.Eq(r => r.Status, ShadowRunStatus.Queued)
                        | (f.Eq(r => r.Status, ShadowRunStatus.Running) & f.Lt(r => r.LeaseUntilUtc, nowUtc)));
        var update = Builders<ShadowAdjudicationRun>.Update
            .Set(r => r.Status, ShadowRunStatus.Running)
            .Set(r => r.LeaseUntilUtc, leaseUntilUtc);
        return _runs.FindOneAndUpdateAsync<ShadowAdjudicationRun?>(filter, update,
            new FindOneAndUpdateOptions<ShadowAdjudicationRun, ShadowAdjudicationRun?>
            {
                Sort = Builders<ShadowAdjudicationRun>.Sort.Ascending(r => r.StartedAtUtc),
                ReturnDocument = ReturnDocument.After,
            }, ct);
    }

    public async Task<IReadOnlyList<ShadowAdjudicationRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default) =>
        await _runs.Find(r => r.TenantId == tenantId)
            .SortByDescending(r => r.StartedAtUtc)
            .Limit(limit)
            .ToListAsync(ct);

    public async Task<ShadowAdjudicationRun?> GetRunAsync(string tenantId, string runId, CancellationToken ct = default) =>
        await _runs.Find(r => r.TenantId == tenantId && r.Id == runId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ShadowAdjudicationResult>> ListResultsAsync(
        string tenantId, string runId, string? kind, int limit, CancellationToken ct = default)
    {
        var f = Builders<ShadowAdjudicationResult>.Filter;
        var filter = f.Eq(r => r.TenantId, tenantId) & f.Eq(r => r.RunId, runId);
        if (!string.IsNullOrWhiteSpace(kind)) filter &= f.Eq(r => r.Kind, kind);
        return await _results.Find(filter)
            .SortByDescending(r => r.AbsolutePaidVariance)
            .Limit(limit)
            .ToListAsync(ct);
    }
}

/// <summary>For tests and a laptop without Mongo.</summary>
public sealed class InMemoryShadowAdjudicationRepository : IShadowAdjudicationRepository
{
    private readonly List<ShadowAdjudicationRun> _runs = [];
    private readonly List<ShadowAdjudicationResult> _results = [];
    private readonly object _sync = new();

    public Task SaveRunAsync(ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default)
    {
        lock (_sync)
        {
            _results.AddRange(results);
            Replace(run);
        }
        return Task.CompletedTask;
    }

    public Task SaveRunStateAsync(ShadowAdjudicationRun run, CancellationToken ct = default)
    {
        lock (_sync) Replace(run);
        return Task.CompletedTask;
    }

    public Task AppendResultsAsync(IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default)
    {
        lock (_sync) _results.AddRange(results);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShadowAdjudicationRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ShadowAdjudicationRun>>(_runs.Where(r => r.TenantId == tenantId)
                .OrderByDescending(r => r.StartedAtUtc).Take(limit).ToList());
    }

    public Task<ShadowAdjudicationRun?> GetRunAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(_runs.FirstOrDefault(r => r.TenantId == tenantId && r.Id == runId));
    }

    public Task<IReadOnlyList<ShadowAdjudicationResult>> ListResultsAsync(
        string tenantId, string runId, string? kind, int limit, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ShadowAdjudicationResult>>(_results
                .Where(r => r.TenantId == tenantId && r.RunId == runId && (string.IsNullOrWhiteSpace(kind) || r.Kind == kind))
                .OrderByDescending(r => r.AbsolutePaidVariance).Take(limit).ToList());
    }

    public Task<IReadOnlySet<string>> ListResultClaimIdsAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlySet<string>>(_results.Where(r => r.TenantId == tenantId && r.RunId == runId)
                .Select(r => r.ClaimId).ToHashSet(StringComparer.Ordinal));
    }

    public Task<ShadowAdjudicationRun?> TakeNextBackgroundRunAsync(DateTime nowUtc, DateTime leaseUntilUtc, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var run = _runs.Where(r => r.Background
                                       && (r.Status == ShadowRunStatus.Queued
                                           || (r.Status == ShadowRunStatus.Running && r.LeaseUntilUtc < nowUtc)))
                .OrderBy(r => r.StartedAtUtc).FirstOrDefault();
            if (run is not null)
            {
                run.Status = ShadowRunStatus.Running;
                run.LeaseUntilUtc = leaseUntilUtc;
            }
            return Task.FromResult(run);
        }
    }

    private void Replace(ShadowAdjudicationRun run)
    {
        _runs.RemoveAll(r => r.TenantId == run.TenantId && r.Id == run.Id);
        _runs.Add(run);
    }
}
