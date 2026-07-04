using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

public interface IMassAdjudicationRunRepository
{
    Task<MassAdjudicationRunSummary> SaveAsync(MassAdjudicationRunSummary summary, CancellationToken ct = default);
    Task<IReadOnlyList<MassAdjudicationRunSummary>> ListAsync(string tenantId, int limit, CancellationToken ct = default);
    Task<MassAdjudicationRunSummary?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<MassAdjudicationClaimResult>> ListClaimResultsAsync(
        string tenantId,
        string runId,
        string? outcome,
        int limit,
        CancellationToken ct = default);
}

public sealed class MassAdjudicationRunRepositoryMongo : IMassAdjudicationRunRepository
{
    internal const string CollectionName = "MassAdjudicationRuns";
    internal const string ClaimResultsCollectionName = "MassAdjudicationClaimResults";
    private readonly IMongoCollection<MassAdjudicationRunSummary> _collection;
    private readonly IMongoCollection<MassAdjudicationClaimResult> _claimResults;

    public MassAdjudicationRunRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<MassAdjudicationRunSummary>(CollectionName);
        _claimResults = database.GetCollection<MassAdjudicationClaimResult>(ClaimResultsCollectionName);
    }

    public async Task<MassAdjudicationRunSummary> SaveAsync(MassAdjudicationRunSummary summary, CancellationToken ct = default)
    {
        summary.Id = Guid.NewGuid().ToString("N");
        summary.CreatedAtUtc = DateTime.UtcNow;

        var claimResults = summary.ClaimResults;
        foreach (var result in claimResults)
        {
            result.Id = Guid.NewGuid().ToString("N");
            result.RunId = summary.Id;
            result.TenantId = summary.Run.TenantId;
            result.CreatedAtUtc = summary.CreatedAtUtc;
        }

        await _collection.InsertOneAsync(summary, cancellationToken: ct);
        if (claimResults.Count > 0)
        {
            try
            {
                await _claimResults.InsertManyAsync(claimResults, cancellationToken: ct);
            }
            catch
            {
                try
                {
                    var filter = Builders<MassAdjudicationRunSummary>.Filter.Eq(x => x.Id, summary.Id);
                    await _collection.DeleteOneAsync(filter, cancellationToken: ct);
                }
                catch
                {
                }

                throw;
            }
        }

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

    public async Task<IReadOnlyList<MassAdjudicationClaimResult>> ListClaimResultsAsync(
        string tenantId,
        string runId,
        string? outcome,
        int limit,
        CancellationToken ct = default)
    {
        var filters = new List<FilterDefinition<MassAdjudicationClaimResult>>
        {
            Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.RunId, runId)
        };

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            filters.Add(Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.Outcome, outcome));
        }

        return await _claimResults
            .Find(Builders<MassAdjudicationClaimResult>.Filter.And(filters))
            .SortByDescending(x => x.ElapsedMilliseconds)
            .Limit(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);
    }
}

public sealed class InMemoryMassAdjudicationRunRepository : IMassAdjudicationRunRepository
{
    private readonly List<MassAdjudicationRunSummary> _runs = new();
    private readonly List<MassAdjudicationClaimResult> _claimResults = new();
    private readonly object _sync = new();

    public Task<MassAdjudicationRunSummary> SaveAsync(MassAdjudicationRunSummary summary, CancellationToken ct = default)
    {
        summary.Id = Guid.NewGuid().ToString("N");
        summary.CreatedAtUtc = DateTime.UtcNow;

        lock (_sync)
        {
            foreach (var result in summary.ClaimResults)
            {
                result.Id = Guid.NewGuid().ToString("N");
                result.RunId = summary.Id;
                result.TenantId = summary.Run.TenantId;
                result.CreatedAtUtc = summary.CreatedAtUtc;
                _claimResults.Add(result);
            }

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

    public Task<IReadOnlyList<MassAdjudicationClaimResult>> ListClaimResultsAsync(
        string tenantId,
        string runId,
        string? outcome,
        int limit,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            var query = _claimResults
                .Where(x => x.TenantId == tenantId && x.RunId == runId);

            if (!string.IsNullOrWhiteSpace(outcome))
            {
                query = query.Where(x => string.Equals(x.Outcome, outcome, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult<IReadOnlyList<MassAdjudicationClaimResult>>(
                query
                    .OrderByDescending(x => x.ElapsedMilliseconds)
                    .Take(Math.Clamp(limit, 1, 1000))
                    .ToList());
        }
    }
}
