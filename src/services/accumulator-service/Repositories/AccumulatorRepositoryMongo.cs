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
        // Indexes: AccumulatorMongoIndexInitializer, once at startup.
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

    public async Task<bool> TryReplaceSnapshotAsync(AccumulatorSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
    {
        snapshot.LastUpdatedDate = DateTime.UtcNow;
        try
        {
            if (expectedVersion == 0)
            {
                // A snapshot first written by a claim: insert, or replace one
                // that exists at version 0 (seeded limits, nothing applied).
                var fresh = Builders<AccumulatorSnapshot>.Filter.And(
                    Builders<AccumulatorSnapshot>.Filter.Eq(s => s.TenantId, snapshot.TenantId),
                    Builders<AccumulatorSnapshot>.Filter.Eq(s => s.Id, snapshot.Id),
                    Builders<AccumulatorSnapshot>.Filter.Eq(s => s.Version, 0L));
                await _snapshots.ReplaceOneAsync(fresh, snapshot, new ReplaceOptions { IsUpsert = true }, ct);
                return true;
            }

            var filter = Builders<AccumulatorSnapshot>.Filter.And(
                Builders<AccumulatorSnapshot>.Filter.Eq(s => s.TenantId, snapshot.TenantId),
                Builders<AccumulatorSnapshot>.Filter.Eq(s => s.Id, snapshot.Id),
                Builders<AccumulatorSnapshot>.Filter.Eq(s => s.Version, expectedVersion));
            var result = await _snapshots.ReplaceOneAsync(filter, snapshot, cancellationToken: ct);
            return result.MatchedCount == 1;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // The upsert found no version-0 document but one with this id exists.
            return false;
        }
    }

    public async Task<bool> TryAppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
    {
        try
        {
            await _events.InsertOneAsync(evt, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AccumulatorEvent>> GetAggregateEventsAsync(
        string tenantId, string aggregateId, long afterVersion = 0, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorEvent>.Filter.And(
            Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.AggregateId, aggregateId),
            Builders<AccumulatorEvent>.Filter.Gt(e => e.Version, afterVersion));
        return await _events.Find(filter).SortBy(e => e.Version).ToListAsync(ct);
    }

    public async Task<AccumulatorEvent?> GetClaimReversedEventAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorEvent>.Filter.And(
            Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimReversed"),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.SourceClaimId, claimId));
        return await _events.Find(filter).FirstOrDefaultAsync(ct);
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

    public async Task<AccumulatorEvent?> GetClaimAppliedEventAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var filter = Builders<AccumulatorEvent>.Filter.And(
            Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimApplied"),
            Builders<AccumulatorEvent>.Filter.Eq(e => e.SourceClaimId, claimId));
        return await _events.Find(filter).SortByDescending(e => e.OccurredAt).FirstOrDefaultAsync(ct);
    }
}

public class ProcessedClaimStoreMongo : IProcessedClaimStore
{
    private readonly IMongoCollection<ProcessedClaim> _col;
    private readonly TimeSpan _lease;
    private readonly TimeProvider _clock;

    public ProcessedClaimStoreMongo(IMongoDatabase database)
        : this(database, ProcessedClaimLease.Timeout, TimeProvider.System)
    {
    }

    public ProcessedClaimStoreMongo(IMongoDatabase database, TimeSpan lease, TimeProvider clock)
    {
        _lease = lease;
        _clock = clock;
        _col = database.GetCollection<ProcessedClaim>(AccumulatorMongoIndexes.ProcessedClaimsCollection);
        // The unique (tenantId, claimId) index: AccumulatorMongoIndexInitializer.
    }

    public async Task ReleaseAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var filter = Builders<ProcessedClaim>.Filter.And(
            Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Id, ProcessedClaim.BuildId(tenantId, claimId)),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Outcome, "Pending"));
        await _col.DeleteOneAsync(filter, ct);
    }

    public async Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default) =>
        (await BeginLeaseAsync(tenantId, claimId, ct)).Outcome;

    public async Task<ClaimLease> BeginLeaseAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var id = ProcessedClaim.BuildId(tenantId, claimId);
        var now = _clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid().ToString("N");
        var marker = new ProcessedClaim
        {
            Id = id,
            TenantId = tenantId,
            ClaimId = claimId,
            ProcessedAt = now,
            Outcome = "Pending",
            LeaseToken = token,
        };
        try
        {
            await _col.InsertOneAsync(marker, cancellationToken: ct);
            return new ClaimLease(BeginClaimOutcome.Proceed, token);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Marker already exists. A terminal outcome is a duplicate. A
            // Pending marker younger than the lease is another attempt in
            // flight (retry later); an older one is a crashed attempt, taken
            // over atomically — only the retry whose update matches proceeds.
            var takeover = Builders<ProcessedClaim>.Filter.And(
                Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
                Builders<ProcessedClaim>.Filter.Eq(p => p.Id, id),
                Builders<ProcessedClaim>.Filter.Eq(p => p.Outcome, "Pending"),
                Builders<ProcessedClaim>.Filter.Lte(p => p.ProcessedAt, now - _lease));
            var taken = await _col.UpdateOneAsync(
                takeover,
                Builders<ProcessedClaim>.Update.Set(p => p.ProcessedAt, now).Set(p => p.LeaseToken, token),
                cancellationToken: ct);
            if (taken.ModifiedCount == 1) return new ClaimLease(BeginClaimOutcome.Proceed, token);

            var existing = await GetAsync(tenantId, claimId, ct);
            if (existing is null) return new ClaimLease(BeginClaimOutcome.InProgress, null);
            return new ClaimLease(string.Equals(existing.Outcome, "Pending", StringComparison.Ordinal)
                ? BeginClaimOutcome.InProgress
                : BeginClaimOutcome.AlreadyApplied, null);
        }
    }

    public async Task<bool> CompleteLeaseAsync(
        string tenantId, string claimId, string leaseToken, string resultingEventId, string outcome,
        string? reversalKind = null, CancellationToken ct = default)
    {
        // The filter is the fence: still Pending, still this attempt's lease.
        var filter = Builders<ProcessedClaim>.Filter.And(
            Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Id, ProcessedClaim.BuildId(tenantId, claimId)),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Outcome, "Pending"),
            Builders<ProcessedClaim>.Filter.Eq(p => p.LeaseToken, leaseToken));
        var update = Builders<ProcessedClaim>.Update
            .Set(p => p.ResultingEventId, resultingEventId)
            .Set(p => p.Outcome, outcome)
            .Set(p => p.ReversalKind, reversalKind)
            .Set(p => p.ProcessedAt, DateTime.UtcNow);
        var result = await _col.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> RecordLeaseTargetAsync(
        string tenantId, string claimId, string leaseToken, LeaseTarget target, CancellationToken ct = default)
    {
        // Same fence as CompleteLeaseAsync: still Pending, still this lease.
        var filter = Builders<ProcessedClaim>.Filter.And(
            Builders<ProcessedClaim>.Filter.Eq(p => p.TenantId, tenantId),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Id, ProcessedClaim.BuildId(tenantId, claimId)),
            Builders<ProcessedClaim>.Filter.Eq(p => p.Outcome, "Pending"),
            Builders<ProcessedClaim>.Filter.Eq(p => p.LeaseToken, leaseToken));
        var update = Builders<ProcessedClaim>.Update
            .Set(p => p.TargetSnapshotId, target.SnapshotId)
            .Set(p => p.TargetMemberId, target.MemberId)
            .Set(p => p.TargetPlanYearStart, target.PlanYearStart)
            .Set(p => p.TargetPlanYearEnd, target.PlanYearEnd);
        var result = await _col.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.MatchedCount == 1;
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
