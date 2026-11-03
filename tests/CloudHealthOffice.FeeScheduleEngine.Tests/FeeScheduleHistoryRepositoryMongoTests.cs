using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// Every create and replace through the MongoDB fee schedule store is recorded:
/// a change listing the codes whose rates moved, and a snapshot of the rates a
/// replace overwrote, so claims paid on them can be found and compared.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class FeeScheduleHistoryRepositoryMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-fs-history";
    private const string ScheduleId = Tenant + ":TMHP adult:20260901";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("fs_history");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private IFeeScheduleHistoryRepository History() =>
        new FeeScheduleHistoryRepositoryMongo(_db, new ConfigurationBuilder().Build());

    private FeeScheduleRepositoryMongo Repo(IFeeScheduleHistoryRepository? history) =>
        new(_db, new ConfigurationBuilder().Build(), NullLogger<FeeScheduleRepositoryMongo>.Instance, history);

    private static FeeSchedule Schedule(decimal rate99213, long revision) => new()
    {
        Id = ScheduleId,
        TenantId = Tenant,
        Name = "TMHP adult",
        Type = FeeScheduleType.Medicaid,
        DefaultForPlanId = "plan-1",
        EffectiveDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        Revision = revision,
        Lines =
        [
            new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = rate99213 },
            new FeeScheduleLine { ProcedureCode = "99214", RateType = FeeScheduleRateType.FlatRate, Rate = 50m },
        ],
    };

    [Fact]
    public async Task CreateThenReplace_RecordsBothChanges_AndSnapshotsTheOldRates()
    {
        var history = History();
        var repo = Repo(history);

        await repo.UpsertAsync(Schedule(33.95m, revision: 0));
        await repo.UpsertAsync(Schedule(35.10m, revision: 1));

        var changes = await history.ListChangesAsync(Tenant, ScheduleId);
        Assert.Equal(2, changes.Count);
        var replaced = changes.Single(c => c.Kind == FeeScheduleChangeKind.Replaced);
        Assert.Equal(1, replaced.PreviousRevision);
        Assert.Equal(2, replaced.NewRevision);
        Assert.Equal(["99213"], replaced.ChangedCodes);
        Assert.Equal(1, replaced.LinesRateIncreased);
        var created = changes.Single(c => c.Kind == FeeScheduleChangeKind.Created);
        Assert.Equal(1, created.NewRevision);

        var snapshot = await history.GetRevisionAsync(Tenant, ScheduleId, 1);
        Assert.NotNull(snapshot);
        Assert.Equal(33.95m, snapshot!.Schedule.Lines.Single(l => l.ProcedureCode == "99213").Rate);
        Assert.Null(await history.GetRevisionAsync(Tenant, ScheduleId, 2));
        Assert.Null(await history.GetRevisionAsync("other-tenant", ScheduleId, 1));
    }

    [Fact]
    public async Task AConflictingReplace_RecordsNothing()
    {
        var history = History();
        var repo = Repo(history);
        await repo.UpsertAsync(Schedule(33.95m, revision: 0));
        await repo.UpsertAsync(Schedule(35.10m, revision: 1));

        await Assert.ThrowsAsync<FeeScheduleConcurrencyException>(() => repo.UpsertAsync(Schedule(20m, revision: 1)));

        Assert.Equal(2, (await history.ListChangesAsync(Tenant, ScheduleId)).Count);
    }

    [Fact]
    public async Task WithoutHistory_TheStoreStillWrites()
    {
        var repo = Repo(history: null);
        await repo.UpsertAsync(Schedule(33.95m, revision: 0));

        Assert.Equal(1, (await repo.GetByIdAsync(Tenant, ScheduleId))!.Revision);
        Assert.Empty(await History().ListChangesAsync(Tenant));
    }
}
