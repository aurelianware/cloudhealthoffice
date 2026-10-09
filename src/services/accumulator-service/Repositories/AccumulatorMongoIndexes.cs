using AccumulatorService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AccumulatorService.Repositories;

/// <summary>
/// The accumulator collections' Mongo indexes, created once at startup by
/// <see cref="AccumulatorMongoIndexInitializer"/> — not in the scoped
/// repositories' constructors, which ran the index commands on every
/// request scope (PR #1278 round 3, M7).
/// </summary>
public static class AccumulatorMongoIndexes
{
    public const string SnapshotsCollection = "AccumulatorSnapshots";
    public const string EventsCollection = "AccumulatorEvents";
    public const string ProcessedClaimsCollection = "AccumulatorProcessedClaims";
    public const string ClaimReversedIndexName = "ux_tenant_sourceClaimId_claimReversed";

    /// <summary>The event type duplicate reversal rows are re-tagged with by <see cref="RetagDuplicateReversalsAsync"/>.</summary>
    public const string DuplicateReversalEventType = "ClaimReversedDuplicate";

    public static async Task EnsureAsync(IMongoDatabase database, ILogger logger, CancellationToken ct = default)
    {
        var snapshots = database.GetCollection<AccumulatorSnapshot>(SnapshotsCollection);
        var events = database.GetCollection<AccumulatorEvent>(EventsCollection);
        var processed = database.GetCollection<ProcessedClaim>(ProcessedClaimsCollection);

        var snapKeys = Builders<AccumulatorSnapshot>.IndexKeys;
        await snapshots.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<AccumulatorSnapshot>(
                snapKeys.Ascending(s => s.TenantId).Ascending(s => s.MemberId).Descending(s => s.PlanYearStart)),
            new CreateIndexModel<AccumulatorSnapshot>(
                snapKeys.Ascending(s => s.TenantId).Ascending(s => s.Id),
                new CreateIndexOptions { Unique = true })
        }, ct);

        var evtKeys = Builders<AccumulatorEvent>.IndexKeys;
        await events.Indexes.CreateManyAsync(new[]
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
                evtKeys.Ascending(e => e.TenantId).Ascending(e => e.MemberId).Descending(e => e.OccurredAt)),
        }, ct);

        // At most one reversal per claim (re-review N5). Rows written before
        // the index existed may already hold duplicates (the double reversal
        // the review found); creating the unique index over them would fail
        // on every start. Re-tag the extras first.
        await RetagDuplicateReversalsAsync(events, logger, ct);
        await events.Indexes.CreateOneAsync(new CreateIndexModel<AccumulatorEvent>(
            evtKeys.Ascending(e => e.TenantId).Ascending(e => e.SourceClaimId),
            new CreateIndexOptions<AccumulatorEvent>
            {
                Name = ClaimReversedIndexName,
                Unique = true,
                PartialFilterExpression = Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimReversed"),
            }), cancellationToken: ct);

        var keys = Builders<ProcessedClaim>.IndexKeys;
        await processed.Indexes.CreateOneAsync(new CreateIndexModel<ProcessedClaim>(
            keys.Ascending(p => p.TenantId).Ascending(p => p.ClaimId),
            new CreateIndexOptions { Unique = true }), cancellationToken: ct);
    }

    /// <summary>
    /// For each (tenant, claim) with more than one <c>ClaimReversed</c> row,
    /// keeps the first (lowest version) and re-tags the rest as
    /// <see cref="DuplicateReversalEventType"/>. The rows stay as the audit
    /// trail; each one is logged, because the snapshot it touched was
    /// reversed more than once and needs an operator adjustment. Returns the
    /// number of rows re-tagged.
    /// </summary>
    public static async Task<int> RetagDuplicateReversalsAsync(
        IMongoCollection<AccumulatorEvent> events, ILogger logger, CancellationToken ct = default)
    {
        // Element names as the driver maps them (no naming convention is
        // assumed).
        var map = MongoDB.Bson.Serialization.BsonClassMap.LookupClassMap(typeof(AccumulatorEvent));
        var tenantField = "$" + map.GetMemberMap(nameof(AccumulatorEvent.TenantId)).ElementName;
        var claimField = "$" + map.GetMemberMap(nameof(AccumulatorEvent.SourceClaimId)).ElementName;
        var duplicates = await events.Aggregate()
            .Match(Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimReversed"))
            .Group(new BsonDocument
            {
                { "_id", new BsonDocument { { "t", tenantField }, { "c", claimField } } },
                { "count", new BsonDocument("$sum", 1) },
            })
            .Match(new BsonDocument("count", new BsonDocument("$gt", 1)))
            .ToListAsync(ct);

        var retagged = 0;
        foreach (var group in duplicates)
        {
            var tenantId = group["_id"]["t"].AsString;
            var claimId = group["_id"]["c"].IsBsonNull ? null : group["_id"]["c"].AsString;
            var rows = await events.Find(Builders<AccumulatorEvent>.Filter.And(
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.TenantId, tenantId),
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.SourceClaimId, claimId),
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.EventType, "ClaimReversed")))
                .SortBy(e => e.Version)
                .ToListAsync(ct);
            foreach (var extra in rows.Skip(1))
            {
                await events.UpdateOneAsync(
                    Builders<AccumulatorEvent>.Filter.Eq(e => e.Id, extra.Id),
                    Builders<AccumulatorEvent>.Update.Set(e => e.EventType, DuplicateReversalEventType),
                    cancellationToken: ct);
                retagged++;
                logger.LogWarning(
                    "Duplicate ClaimReversed row {EventId} for claim {ClaimId} (snapshot {SnapshotId} v{Version}) " +
                    "re-tagged {EventType}: the snapshot was reversed more than once and needs a manual adjustment " +
                    "of deductible {Deductible} / OOP {Oop}",
                    extra.Id, claimId, extra.AggregateId, extra.Version, DuplicateReversalEventType,
                    -extra.DeductibleDelta, -extra.OopDelta);
            }
        }
        return retagged;
    }
}

/// <summary>Creates the accumulator Mongo indexes once when the service starts.</summary>
public sealed class AccumulatorMongoIndexInitializer(
    IServiceScopeFactory scopes, ILogger<AccumulatorMongoIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        // Hosts that replace the repositories (tests) register no database.
        var database = scope.ServiceProvider.GetService<IMongoDatabase>();
        if (database is null)
        {
            logger.LogWarning("No IMongoDatabase registered; accumulator indexes not created");
            return;
        }
        await AccumulatorMongoIndexes.EnsureAsync(database, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
