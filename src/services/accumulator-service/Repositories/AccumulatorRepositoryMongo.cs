using AccumulatorService.Models;
using MongoDB.Driver;

namespace AccumulatorService.Repositories;

public class AccumulatorRepositoryMongo : IAccumulatorRepository
{
    private readonly IMongoCollection<AccumulatorSnapshot> _snapshots;
    private readonly IMongoCollection<AccumulatorEvent> _events;

    public AccumulatorRepositoryMongo(IMongoDatabase database)
    {
        _snapshots = database.GetCollection<AccumulatorSnapshot>("AccumulatorSnapshots");
        _events = database.GetCollection<AccumulatorEvent>("AccumulatorEvents");
        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        var snapKeys = Builders<AccumulatorSnapshot>.IndexKeys;
        _snapshots.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<AccumulatorSnapshot>(
                snapKeys.Ascending(s => s.TenantId).Ascending(s => s.MemberId).Descending(s => s.PlanYearStart)),
            new CreateIndexModel<AccumulatorSnapshot>(
                snapKeys.Ascending(s => s.TenantId).Ascending(s => s.Id),
                new CreateIndexOptions { Unique = true })
        });

        var evtKeys = Builders<AccumulatorEvent>.IndexKeys;
        _events.Indexes.CreateMany(new[]
        {
            // Wire-level de-dup. (tenantId, eventId) must be globally unique.
            new CreateIndexModel<AccumulatorEvent>(
                evtKeys.Ascending(e => e.TenantId).Ascending(e => e.EventId),
                new CreateIndexOptions { Unique = true }),
            // Per-aggregate ordering. (tenantId, aggregateId, version) must be unique.
            new CreateIndexModel<AccumulatorEvent>(
                evtKeys.Ascending(e => e.TenantId).Ascending(e => e.AggregateId).Ascending(e => e.Version),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<AccumulatorEvent>(
                evtKeys.Ascending(e => e.TenantId).Ascending(e => e.MemberId).Descending(e => e.OccurredAt))
        });
    }

    public async Task<AccumulatorSnapshot?> GetSnapshotAsync(string tenantId, string memberId, DateTime planYearStart, CancellationToken ct = default)
    {
        var id = AccumulatorSnapshot.BuildId(tenantId, memberId, planYearStart);
        var filter = Builders<AccumulatorSnapshot>.Filter.And(
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.Id, id));
        return await _snapshots.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<AccumulatorSnapshot?> GetSnapshotByAsOfDateAsync(string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorSnapshot>.Filter.And(
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.MemberId, memberId),
            Builders<AccumulatorSnapshot>.Filter.Lte(s => s.PlanYearStart, asOfDate),
            Builders<AccumulatorSnapshot>.Filter.Gte(s => s.PlanYearEnd, asOfDate));
        return await _snapshots.Find(filter).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<AccumulatorSnapshot>> GetSnapshotsAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorSnapshot>.Filter.And(
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.TenantId, tenantId),
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.MemberId, memberId));
        return await _snapshots.Find(filter)
            .SortByDescending(s => s.PlanYearStart)
            .ToListAsync(ct);
    }

    public async Task UpsertSnapshotAsync(AccumulatorSnapshot snapshot, CancellationToken ct = default)
    {
        snapshot.LastUpdatedDate = DateTime.UtcNow;
        var filter = Builders<AccumulatorSnapshot>.Filter.And(
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.TenantId, snapshot.TenantId),
            Builders<AccumulatorSnapshot>.Filter.Eq(s => s.Id, snapshot.Id));
        await _snapshots.ReplaceOneAsync(filter, snapshot, new ReplaceOptions { IsUpsert = true }, ct);
    }

    public async Task AppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
    {
        await _events.InsertOneAsync(evt, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<AccumulatorEvent>> GetEventsAsync(string tenantId, string memberId, int take = 100, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorEvent>.Filter.And(
            Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.MemberId, memberId));
        return await _events.Find(filter)
            .SortByDescending(e => e.OccurredAt)
            .Limit(take)
            .ToListAsync(ct);
    }

    public async Task<AccumulatorEvent?> GetManualAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorEvent>.Filter.And(
            Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ManualAdjustment"),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.SourceReference, adjustmentId));
        return await _events.Find(filter).FirstOrDefaultAsync(ct);
    }
}

public class ProcessedClaimStoreMongo : IProcessedClaimStore
{
    private readonly IMongoCollection<ProcessedClaim> _col;

    public ProcessedClaimStoreMongo(IMongoDatabase database)
    {
        _col = database.GetCollection<ProcessedClaim>("AccumulatorProcessedClaims");
        var keys = Builders<ProcessedClaim>.IndexKeys;
        _col.Indexes.CreateOne(new CreateIndexModel<ProcessedClaim>(
            keys.Ascending(p => p.TenantId).Ascending(p => p.ClaimId),
            new CreateIndexOptions { Unique = true }));
    }

    public async Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var marker = new ProcessedClaim
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            ProcessedAt = DateTime.UtcNow,
            Outcome = "Pending"
        };
        try
        {
            await _col.InsertOneAsync(marker, cancellationToken: ct);
            return BeginClaimOutcome.Proceed;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Marker already exists. Only treat as duplicate if a prior attempt
            // reached a terminal outcome; Pending means an earlier attempt crashed
            // mid-flight and the caller may retry.
            var existing = await GetAsync(tenantId, claimId, ct);
            if (existing is null || string.Equals(existing.Outcome, "Pending", StringComparison.Ordinal))
            {
                return BeginClaimOutcome.Proceed;
            }
            return BeginClaimOutcome.AlreadyApplied;
        }
    }

    public async Task CompleteAsync(string tenantId, string claimId, string resultingEventId, string outcome, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var filter = Builders<ProcessedClaim>.Filter.And(
            Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Id, id));
        var update = Builders<ProcessedClaim>.Update
            .Set(p => p.ResultingEventId, resultingEventId)
            .Set(p => p.Outcome, outcome)
            .Set(p => p.ProcessedAt, DateTime.UtcNow);
        await _col.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var filter = Builders<ProcessedClaim>.Filter.And(
            Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Id, id));
        return await _col.Find(filter).FirstOrDefaultAsync(ct);
    }
}
