using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

public interface IMassAdjudicationRunRepository
{
    Task<MassAdjudicationRunSummary> SaveAsync(MassAdjudicationRunSummary summary, CancellationToken ct = default);
    Task<IReadOnlyList<MassAdjudicationRunSummary>> ListAsync(string tenantId, int limit, CancellationToken ct = default);
    Task<MassAdjudicationRunSummary?> GetAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class MassAdjudicationRunRepositoryMongo : IMassAdjudicationRunRepository
{
    private readonly IMongoCollection<MassAdjudicationRunSummary> _collection;

    public MassAdjudicationRunRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<MassAdjudicationRunSummary>("MassAdjudicationRuns");

        var keys = Builders<MassAdjudicationRunSummary>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<MassAdjudicationRunSummary>(
                keys.Ascending(x => x.Run.TenantId).Descending(x => x.Run.StartedAtUtc),
                new CreateIndexOptions { Name = "tenant_started_desc" }),
            new CreateIndexModel<MassAdjudicationRunSummary>(
                keys.Ascending(x => x.Run.TenantId).Ascending(x => x.Id),
                new CreateIndexOptions { Name = "tenant_run_id" })
        });
    }

    public async Task<MassAdjudicationRunSummary> SaveAsync(MassAdjudicationRunSummary summary, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(summary.Id))
        {
            summary.Id = Guid.NewGuid().ToString("N");
        }

        summary.CreatedAtUtc = DateTime.UtcNow;
        await _collection.InsertOneAsync(summary, cancellationToken: ct);
        return summary;
    }

    public async Task<IReadOnlyList<MassAdjudicationRunSummary>> ListAsync(
        string tenantId,
        int limit,
        CancellationToken ct = default)
    {
        var filter = Builders<MassAdjudicationRunSummary>.Filter.Eq(x => x.Run.TenantId, tenantId);
        return await _collection.Find(filter)
            .SortByDescending(x => x.Run.StartedAtUtc)
            .Limit(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);
    }

    public Task<MassAdjudicationRunSummary?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var filter = Builders<MassAdjudicationRunSummary>.Filter.And(
            Builders<MassAdjudicationRunSummary>.Filter.Eq(x => x.Run.TenantId, tenantId),
            Builders<MassAdjudicationRunSummary>.Filter.Eq(x => x.Id, id));

        return _collection.Find(filter).FirstOrDefaultAsync(ct)!;
    }
}

public sealed class InMemoryMassAdjudicationRunRepository : IMassAdjudicationRunRepository
{
    private readonly List<MassAdjudicationRunSummary> _runs = new();
    private readonly object _sync = new();

    public Task<MassAdjudicationRunSummary> SaveAsync(MassAdjudicationRunSummary summary, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(summary.Id))
        {
            summary.Id = Guid.NewGuid().ToString("N");
        }

        summary.CreatedAtUtc = DateTime.UtcNow;

        lock (_sync)
        {
            _runs.Add(summary);
        }

        return Task.FromResult(summary);
    }

    public Task<IReadOnlyList<MassAdjudicationRunSummary>> ListAsync(
        string tenantId,
        int limit,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<MassAdjudicationRunSummary>>(
                _runs
                    .Where(x => x.Run.TenantId == tenantId)
                    .OrderByDescending(x => x.Run.StartedAtUtc)
                    .Take(Math.Clamp(limit, 1, 100))
                    .ToList());
        }
    }

    public Task<MassAdjudicationRunSummary?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_sync)
        {
            return Task.FromResult(
                _runs.FirstOrDefault(x => x.Run.TenantId == tenantId && x.Id == id));
        }
    }
}
