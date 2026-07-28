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
        string? paymentStatus,
        decimal paymentTolerance,
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
    internal const int ClaimResultInsertBatchSize = 50;
    private const int MaxRateLimitAttempts = 6;
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
            foreach (var batch in claimResults.Chunk(ClaimResultInsertBatchSize))
            {
                await InsertClaimResultBatchAsync(batch, ct);
            }

            var newResultIds = claimResults.Select(x => x.Id).ToArray();
            var staleResultFilter = Builders<MassAdjudicationClaimResult>.Filter.And(
                Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.TenantId, summary.Run.TenantId),
                Builders<MassAdjudicationClaimResult>.Filter.Eq(x => x.RunId, summary.Id),
                Builders<MassAdjudicationClaimResult>.Filter.Nin(x => x.Id, newResultIds));
            await _claimResults.DeleteManyAsync(staleResultFilter, ct);
        }

        return summary;
    }

    private async Task InsertClaimResultBatchAsync(
        IReadOnlyCollection<MassAdjudicationClaimResult> batch,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _claimResults.InsertManyAsync(
                    batch,
                    new InsertManyOptions { IsOrdered = true },
                    ct);
                return;
            }
            catch (Exception ex) when (IsCosmosRateLimit(ex) && attempt < MaxRateLimitAttempts)
            {
                // Cosmos DB for MongoDB reports throttling as code 16500.
                // Keep retries local to a small batch so a free-tier account
                // can recover without replaying the entire run summary.
                var delay = TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1)));
                await Task.Delay(delay, ct);
            }
        }
    }

    internal static bool IsCosmosRateLimit(Exception exception) =>
        exception switch
        {
            MongoCommandException command => command.Code == 16500,
            MongoWriteException write => write.WriteError?.Code == 16500,
            MongoBulkWriteException<MassAdjudicationClaimResult> bulk =>
                bulk.WriteErrors.Any(error => error.Code == 16500),
            _ => false
        };

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
        string? paymentStatus,
        decimal paymentTolerance,
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

        AddPaymentStatusFilter(filters, paymentStatus, paymentTolerance);

        return await _claimResults
            .Find(Builders<MassAdjudicationClaimResult>.Filter.And(filters))
            .SortByDescending(x => x.ElapsedMilliseconds)
            .Limit(Math.Clamp(limit, 1, 1000))
            .ToListAsync(ct);
    }

    private static void AddPaymentStatusFilter(
        List<FilterDefinition<MassAdjudicationClaimResult>> filters,
        string? paymentStatus,
        decimal paymentTolerance)
    {
        if (string.IsNullOrWhiteSpace(paymentStatus))
        {
            return;
        }

        var tolerance = MassAdjudicationPaymentTolerance.Normalize(paymentTolerance);
        var builder = Builders<MassAdjudicationClaimResult>.Filter;
        var hasPaymentDelta = builder.Exists(x => x.PaymentDelta, true);
        var hasNoPaymentDelta = builder.Or(
            builder.Exists(x => x.PaymentDelta, false),
            builder.Eq(x => x.PaymentDelta, null));

        switch (paymentStatus.Trim().ToLowerInvariant())
        {
            case "mismatched":
                filters.Add(builder.And(
                    hasPaymentDelta,
                    builder.Ne(x => x.PaymentDelta, null),
                    builder.Gt(x => x.PaymentDelta, tolerance)));
                break;
            case "matched":
                filters.Add(builder.And(
                    hasPaymentDelta,
                    builder.Ne(x => x.PaymentDelta, null),
                    builder.Lte(x => x.PaymentDelta, tolerance)));
                break;
            case "scored":
                filters.Add(builder.And(
                    hasPaymentDelta,
                    builder.Ne(x => x.PaymentDelta, null)));
                break;
            case "unscored":
                filters.Add(hasNoPaymentDelta);
                break;
        }
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

        foreach (var result in summary.ClaimResults)
        {
            result.Id = Guid.NewGuid().ToString("N");
            result.RunId = summary.Id;
            result.TenantId = summary.Run.TenantId;
            result.CreatedAtUtc = summary.CreatedAtUtc;
        }

        lock (_sync)
        {
            _runs.RemoveAll(x => x.Run.TenantId == summary.Run.TenantId && x.Id == summary.Id);
            if (summary.ClaimResults.Count > 0)
            {
                var newResultIds = summary.ClaimResults
                    .Select(x => x.Id)
                    .ToHashSet(StringComparer.Ordinal);
                _claimResults.RemoveAll(x =>
                    x.TenantId == summary.Run.TenantId
                    && x.RunId == summary.Id
                    && !newResultIds.Contains(x.Id));
            }

            foreach (var result in summary.ClaimResults)
            {
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
        string? paymentStatus,
        decimal paymentTolerance,
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

            query = ApplyPaymentStatusFilter(query, paymentStatus, paymentTolerance);

            return Task.FromResult<IReadOnlyList<MassAdjudicationClaimResult>>(
                query
                    .OrderByDescending(x => x.ElapsedMilliseconds)
                    .Take(Math.Clamp(limit, 1, 1000))
                    .ToList());
        }
    }

    private static IEnumerable<MassAdjudicationClaimResult> ApplyPaymentStatusFilter(
        IEnumerable<MassAdjudicationClaimResult> query,
        string? paymentStatus,
        decimal paymentTolerance)
    {
        var tolerance = MassAdjudicationPaymentTolerance.Normalize(paymentTolerance);
        return paymentStatus?.Trim().ToLowerInvariant() switch
        {
            "mismatched" => query.Where(x => x.PaymentDelta > tolerance),
            "matched" => query.Where(x => x.PaymentDelta <= tolerance),
            "scored" => query.Where(x => x.PaymentDelta.HasValue),
            "unscored" => query.Where(x => !x.PaymentDelta.HasValue),
            _ => query
        };
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

internal static class MassAdjudicationPaymentTolerance
{
    internal const decimal LegacyDefault = 0.01m;

    internal static decimal Normalize(decimal paymentTolerance)
        => paymentTolerance > 0 ? paymentTolerance : LegacyDefault;
}
