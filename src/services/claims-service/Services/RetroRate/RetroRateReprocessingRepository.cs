using MongoDB.Driver;

namespace ClaimsService.Services.RetroRate;

/// <summary>Retro rate reprocessing runs (with their items).</summary>
public interface IRetroRateReprocessingRepository
{
    Task CreateAsync(RetroRateReprocessingRun run, CancellationToken ct = default);
    Task<RetroRateReprocessingRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default);
    Task<IReadOnlyList<RetroRateReprocessingRun>> ListAsync(string tenantId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="run"/> only while the stored
    /// <see cref="RetroRateReprocessingRun.ConcurrencyRevision"/> is still
    /// <paramref name="expectedRevision"/>; stores it + 1. False when another
    /// write got there first.
    /// </summary>
    Task<bool> TryReplaceAsync(RetroRateReprocessingRun run, long expectedRevision, CancellationToken ct = default);
}

public sealed class RetroRateReprocessingRepositoryMongo : IRetroRateReprocessingRepository
{
    public const string CollectionName = "RetroRateReprocessingRuns";

    private readonly IMongoCollection<RetroRateReprocessingRun> _runs;

    public RetroRateReprocessingRepositoryMongo(IMongoDatabase database) =>
        _runs = database.GetCollection<RetroRateReprocessingRun>(CollectionName);

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default) =>
        database.GetCollection<RetroRateReprocessingRun>(CollectionName).Indexes.CreateOne(
            new CreateIndexModel<RetroRateReprocessingRun>(
                Builders<RetroRateReprocessingRun>.IndexKeys.Ascending(r => r.TenantId).Descending(r => r.CreatedAtUtc),
                new CreateIndexOptions { Name = "tenant_created_desc" }),
            cancellationToken: ct);

    public Task CreateAsync(RetroRateReprocessingRun run, CancellationToken ct = default) =>
        _runs.InsertOneAsync(run, cancellationToken: ct);

    public async Task<RetroRateReprocessingRun?> GetAsync(string tenantId, string runId, CancellationToken ct = default) =>
        await _runs.Find(r => r.TenantId == tenantId && r.Id == runId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<RetroRateReprocessingRun>> ListAsync(string tenantId, int limit, CancellationToken ct = default) =>
        await _runs.Find(r => r.TenantId == tenantId)
            .SortByDescending(r => r.CreatedAtUtc).Limit(limit).ToListAsync(ct);

    public async Task<bool> TryReplaceAsync(RetroRateReprocessingRun run, long expectedRevision, CancellationToken ct = default)
    {
        run.ConcurrencyRevision = expectedRevision + 1;
        var f = Builders<RetroRateReprocessingRun>.Filter;
        var result = await _runs.ReplaceOneAsync(
            f.Eq(r => r.TenantId, run.TenantId) & f.Eq(r => r.Id, run.Id) & f.Eq(r => r.ConcurrencyRevision, expectedRevision),
            run, cancellationToken: ct);
        if (result.MatchedCount == 1) return true;
        run.ConcurrencyRevision = expectedRevision;
        return false;
    }
}
