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
        string? validationStatus,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListSubmittedClaimIdsAsync(
        string tenantId,
        string runId,
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
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(summary.Id))
        {
            summary.Id = Guid.NewGuid().ToString("N");
        }

        if (summary.CreatedAtUtc == default)
        {
            summary.CreatedAtUtc = now;
        }

        summary.LastUpdatedAtUtc = now;

        var claimResults = summary.ClaimResults;
        foreach (var result in claimResults)
        {
            result.Id = Guid.NewGuid().ToString("N");
            result.RunId = summary.Id;
            result.TenantId = summary.Run.TenantId;
            result.CreatedAtUtc = summary.CreatedAtUtc;
        }

        var runFilter = Builders<MassAdjudicationRunSummary>.Filter.And(
            Builders<MassAdjudicationRunSummary>.Filter.Eq(x => x.Run.TenantId, summary.Run.TenantId),
            Builders<MassAdjudicationRunSummary>.Filter.Eq(x => x.Id, summary.Id));

        await _collection.ReplaceOneAsync(
            runFilter,
            summary,
            new ReplaceOptions { IsUpsert = true },
            ct);

        if (claimResults.Count > 0)
        {
            try
            {
                var resultFilter = Builders<MassAdjudicationClaimResult>.Filter.And(
                    Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.TenantId, summary.Run.TenantId),
                    Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.RunId, summary.Id));
                await _claimResults.DeleteManyAsync(resultFilter, ct);
                await _claimResults.InsertManyAsync(claimResults, cancellationToken: ct);
            }
            catch
            {
                try
                {
                    await _collection.DeleteOneAsync(runFilter, cancellationToken: ct);
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
        string? validationStatus,
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

        if (!string.IsNullOrWhiteSpace(validationStatus))
        {
            filters.Add(Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.ValidationStatus, validationStatus));
        }

        return await _claimResults
            .Find(Builders<MassAdjudicationClaimResult>.Filter.And(filters))
            .SortByDescending(x => x.ElapsedMilliseconds)
            .Limit(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListSubmittedClaimIdsAsync(
        string tenantId,
        string runId,
        CancellationToken ct = default)
    {
        var filter = Builders<MassAdjudicationClaimResult>.Filter.And(
            Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.RunId, runId),
            Builders<MassAdjudicationClaimResult>.Filter.Ne(x => x.SubmittedClaimId, null),
            Builders<MassAdjudicationClaimResult>.Filter.Ne(x => x.SubmittedClaimId, string.Empty));

        return await _claimResults
            .Find(filter)
            .Project(x => x.SubmittedClaimId!)
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
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(summary.Id))
        {
            summary.Id = Guid.NewGuid().ToString("N");
        }

        if (summary.CreatedAtUtc == default)
        {
            summary.CreatedAtUtc = now;
        }

        summary.LastUpdatedAtUtc = now;

        lock (_sync)
        {
            _runs.RemoveAll(x => x.Run.TenantId == summary.Run.TenantId && x.Id == summary.Id);
            if (summary.ClaimResults.Count > 0)
            {
                _claimResults.RemoveAll(x => x.TenantId == summary.Run.TenantId && x.RunId == summary.Id);
            }

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
        string? validationStatus,
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

            if (!string.IsNullOrWhiteSpace(validationStatus))
            {
                query = query.Where(x => string.Equals(x.ValidationStatus, validationStatus, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult<IReadOnlyList<MassAdjudicationClaimResult>>(
                query
                    .OrderByDescending(x => x.ElapsedMilliseconds)
                    .Take(Math.Clamp(limit, 1, 1000))
                    .ToList());
        }
    }

    public Task<IReadOnlyList<string>> ListSubmittedClaimIdsAsync(
        string tenantId,
        string runId,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                _claimResults
                    .Where(x => x.TenantId == tenantId && x.RunId == runId)
                    .Select(x => x.SubmittedClaimId)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());
        }
    }
}
