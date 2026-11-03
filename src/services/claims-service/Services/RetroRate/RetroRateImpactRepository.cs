using MongoDB.Driver;

namespace ClaimsService.Services.RetroRate;

/// <summary>
/// Retro rate impact runs and their per-claim results. Separate collections
/// from the claims: a dry run never changes a claim.
/// </summary>
public interface IRetroRateImpactRepository
{
    Task SaveRunAsync(RetroRateImpactRun run, IReadOnlyList<RetroRateImpactResult> results, CancellationToken ct = default);
    Task<IReadOnlyList<RetroRateImpactRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default);
    Task<RetroRateImpactRun?> GetRunAsync(string tenantId, string runId, CancellationToken ct = default);

    /// <summary>A run's results, largest absolute paid difference first, optionally one <see cref="RetroRateDirection"/>.</summary>
    Task<IReadOnlyList<RetroRateImpactResult>> ListResultsAsync(
        string tenantId, string runId, string? direction, int limit, CancellationToken ct = default);
}

public sealed class RetroRateImpactRepositoryMongo : IRetroRateImpactRepository
{
    public const string RunsCollectionName = "RetroRateImpactRuns";
    public const string ResultsCollectionName = "RetroRateImpactResults";
    private const int InsertBatchSize = 100;

    private readonly IMongoCollection<RetroRateImpactRun> _runs;
    private readonly IMongoCollection<RetroRateImpactResult> _results;

    public RetroRateImpactRepositoryMongo(IMongoDatabase database)
    {
        _runs = database.GetCollection<RetroRateImpactRun>(RunsCollectionName);
        _results = database.GetCollection<RetroRateImpactResult>(ResultsCollectionName);
    }

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default)
    {
        database.GetCollection<RetroRateImpactRun>(RunsCollectionName).Indexes.CreateOne(
            new CreateIndexModel<RetroRateImpactRun>(
                Builders<RetroRateImpactRun>.IndexKeys.Ascending(r => r.TenantId).Descending(r => r.StartedAtUtc),
                new CreateIndexOptions { Name = "tenant_started_desc" }),
            cancellationToken: ct);
        database.GetCollection<RetroRateImpactResult>(ResultsCollectionName).Indexes.CreateOne(
            new CreateIndexModel<RetroRateImpactResult>(
                Builders<RetroRateImpactResult>.IndexKeys
                    .Ascending(r => r.TenantId).Ascending(r => r.RunId).Ascending(r => r.Direction),
                new CreateIndexOptions { Name = "tenant_run_direction" }),
            cancellationToken: ct);
    }

    public async Task SaveRunAsync(
        RetroRateImpactRun run, IReadOnlyList<RetroRateImpactResult> results, CancellationToken ct = default)
    {
        foreach (var batch in results.Chunk(InsertBatchSize))
            await _results.InsertManyAsync(batch, cancellationToken: ct);

        await _runs.ReplaceOneAsync(
            Builders<RetroRateImpactRun>.Filter.Eq(r => r.TenantId, run.TenantId)
            & Builders<RetroRateImpactRun>.Filter.Eq(r => r.Id, run.Id),
            run, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task<IReadOnlyList<RetroRateImpactRun>> ListRunsAsync(string tenantId, int limit, CancellationToken ct = default) =>
        await _runs.Find(r => r.TenantId == tenantId).SortByDescending(r => r.StartedAtUtc).Limit(limit).ToListAsync(ct);

    public async Task<RetroRateImpactRun?> GetRunAsync(string tenantId, string runId, CancellationToken ct = default) =>
        await _runs.Find(r => r.TenantId == tenantId && r.Id == runId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<RetroRateImpactResult>> ListResultsAsync(
        string tenantId, string runId, string? direction, int limit, CancellationToken ct = default)
    {
        var f = Builders<RetroRateImpactResult>.Filter;
        var filter = f.Eq(r => r.TenantId, tenantId) & f.Eq(r => r.RunId, runId);
        if (!string.IsNullOrWhiteSpace(direction))
            filter &= f.Eq(r => r.Direction, direction);
        return await _results.Find(filter)
            .SortByDescending(r => r.AbsoluteNetPaidDelta).ThenBy(r => r.ClaimNumber)
            .Limit(limit).ToListAsync(ct);
    }
}
