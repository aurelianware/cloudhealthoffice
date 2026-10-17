using AccumulatorService.Models;
using AccumulatorService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// Plan-year boundaries are calendar dates and must read back as the same
/// date on any host. The driver's default DateTime handling stored an
/// Unspecified 2025-01-01 as 2025-01-01T07:00Z on a UTC-7 host, and as
/// 2024-12-31T22:00Z on a UTC+2 host — where the read-back start then built
/// the id of the previous plan year's snapshot. Run on a non-UTC host (CI's
/// TZ=America/Phoenix leg) these fail without the calendar-date serializer.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AccumulatorPlanYearDateTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private static readonly DateTime Start = new(2025, 1, 1);
    private static readonly DateTime End = new(2025, 12, 31);
    private static readonly DateTime MidnightUtc = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private IMongoDatabase _db = null!;
    private AccumulatorRepositoryMongo _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("accumulators_plan_year_dates");
        await AccumulatorMongoIndexes.EnsureAsync(_db, NullLogger.Instance);
        _repo = new AccumulatorRepositoryMongo(_db);
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private async Task<BsonDocument> RawSingle(string collectionContaining)
    {
        var names = await (await _db.ListCollectionNamesAsync()).ToListAsync();
        var name = Assert.Single(names, n => n.Contains(collectionContaining, StringComparison.OrdinalIgnoreCase));
        return await _db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
    }

    [Fact]
    public async Task Snapshot_PlanYear_IsStoredAsMidnightUtc_AndReadsBackAsTheSameDates()
    {
        var id = AccumulatorSnapshot.BuildId(Tenant, "m1", Start);
        Assert.True(await _repo.TryReplaceSnapshotAsync(new AccumulatorSnapshot
        {
            Id = id, TenantId = Tenant, MemberId = "m1", PlanYearStart = Start, PlanYearEnd = End,
        }, expectedVersion: 0));

        var raw = await RawSingle("snapshot");
        Assert.Equal(MidnightUtc, raw["PlanYearStart"].ToUniversalTime());

        var back = (await _repo.GetSnapshotAsync(Tenant, "m1", Start))!;
        Assert.Equal(Start, back.PlanYearStart);
        Assert.Equal(End, back.PlanYearEnd);
        Assert.Equal(id, AccumulatorSnapshot.BuildId(Tenant, "m1", back.PlanYearStart));

        var byAsOf = await _repo.GetSnapshotByAsOfDateAsync(Tenant, "m1", End);
        Assert.Equal(id, byAsOf?.Id);
    }

    [Fact]
    public async Task LeaseTarget_PlanYear_ReadsBackAsTheSameDates()
    {
        var store = new ProcessedClaimStoreMongo(_db, TimeSpan.FromMinutes(2), TimeProvider.System);
        var lease = await store.BeginLeaseAsync(Tenant, "C1");
        var target = new LeaseTarget(AccumulatorSnapshot.BuildId(Tenant, "m1", Start), "m1", Start, End);

        Assert.True(await store.RecordLeaseTargetAsync(Tenant, "C1", lease.Token!, target));

        var marker = (await store.GetAsync(Tenant, "C1"))!;
        Assert.Equal(Start, marker.TargetPlanYearStart);
        Assert.Equal(End, marker.TargetPlanYearEnd);
        Assert.Equal(target, LeaseTarget.From(marker));
    }
}
