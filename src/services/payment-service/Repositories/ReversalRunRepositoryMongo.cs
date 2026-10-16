using MongoDB.Driver;
using PaymentService.Models;

namespace PaymentService.Repositories;

public class ReversalRunRepositoryMongo : IReversalRunRepository
{
    private readonly IMongoCollection<ReversalRun> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ReversalRunRepositoryMongo> _logger;

    public ReversalRunRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ReversalRunRepositoryMongo> logger)
    {
        _collection = database.GetCollection<ReversalRun>("ReversalRuns");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<ReversalRun?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<ReversalRun?> GetByReversalRunNumberAsync(string reversalRunNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ReversalRun>.Filter.Eq(x => x.ReversalRunNumber, reversalRunNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<ReversalRun>>
        {
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ReversalRun>.Filter.Gte(x => x.CreatedAt, from),
            Builders<ReversalRun>.Filter.Lte(x => x.CreatedAt, to)
        };

        if (status.HasValue)
            filters.Add(Builders<ReversalRun>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<ReversalRun>.Filter.And(filters))
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<ReversalRun> CreateAsync(ReversalRun reversalRun)
    {
        reversalRun.TenantId = GetTenantId();
        reversalRun.CreatedAt = DateTime.UtcNow;
        reversalRun.Revision = 0;
        await _collection.InsertOneAsync(reversalRun);
        _logger.LogInformation("Created reversal run {ReversalRunNumber}", reversalRun.ReversalRunNumber);
        return reversalRun;
    }

    /// <summary>Every write to a run document bumps its <see cref="ReversalRun.Revision"/>.</summary>
    private static UpdateDefinition<ReversalRun> Bumped(UpdateDefinition<ReversalRun> update) =>
        update.Inc(x => x.Revision, 1L);

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<ReversalRun> RevisionIs(long expected)
    {
        var b = Builders<ReversalRun>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<ReversalRun>.Filter.Eq(x => x.Status, ReversalRunStatus.Pending));
        var update = Builders<ReversalRun>.Update
            .Set(x => x.Status, ReversalRunStatus.Running)
            .Set(x => x.ExecutedBy, executedBy)
            .Set(x => x.ExecutionStartedAt, startedAt);
        var result = await _collection.UpdateOneAsync(filter, Bumped(update));
        return result.ModifiedCount == 1;
    }

    public async Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId));

        var touched = outcomes.TouchedClaimIds;
        if (touched.Count > 0)
        {
            var pulled = await _collection.UpdateOneAsync(filter, Bumped(Builders<ReversalRun>.Update.PullFilter(
                x => x.ReservationsNeedingAttention,
                Builders<ReservationAttention>.Filter.In(a => a.ClaimId, touched))));
            if (pulled.MatchedCount == 0)
                return false;
        }

        var updates = new List<UpdateDefinition<ReversalRun>>();
        if (outcomes.Released.Count > 0)
            updates.Add(Builders<ReversalRun>.Update.AddToSetEach(x => x.ReleasedReservationClaimIds, outcomes.Released));
        if (outcomes.Attention.Count > 0)
            updates.Add(Builders<ReversalRun>.Update.PushEach(x => x.ReservationsNeedingAttention, outcomes.Attention));
        if (outcomes.Warnings.Count > 0)
            updates.Add(Builders<ReversalRun>.Update.PushEach(x => x.Warnings, outcomes.Warnings));
        if (updates.Count == 0)
            return true;

        var result = await _collection.UpdateOneAsync(filter, Bumped(Builders<ReversalRun>.Update.Combine(updates)));
        return result.MatchedCount == 1;
    }

    public async Task<ReversalRun> UpdateAsync(ReversalRun reversalRun)
    {
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, reversalRun.Id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, reversalRun.TenantId));

        // Conditional on the revision the caller read: a targeted write in
        // between (Pending -> Running, reservation outcomes) or another save
        // makes this one fail instead of being silently undone by the replace.
        var expected = reversalRun.Revision;
        reversalRun.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), reversalRun);
        }
        catch
        {
            reversalRun.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            reversalRun.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new PaymentConcurrencyException("Reversal run", reversalRun.Id, expected);
            // Missing: as before, nothing is written and the run is returned.
        }

        _logger.LogInformation("Updated reversal run {ReversalRunNumber}", reversalRun.ReversalRunNumber);
        return reversalRun;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<ReversalRun>.Filter.And(
            Builders<ReversalRun>.Filter.Eq(x => x.Id, id),
            Builders<ReversalRun>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted reversal run {Id}", id);
    }
}
