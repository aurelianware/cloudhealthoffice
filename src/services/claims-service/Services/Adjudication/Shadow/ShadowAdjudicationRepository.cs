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
