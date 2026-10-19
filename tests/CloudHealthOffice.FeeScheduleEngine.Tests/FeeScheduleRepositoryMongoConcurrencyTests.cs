using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// Optimistic concurrency on the MongoDB fee schedule and provider contract
/// upserts, against a real mongod. <c>UpsertAsync</c> replaced the document on
/// id + tenant with no condition, so of two concurrent replaces of a plan's
/// default schedule (or two first-time creates) the later silently erased the
/// earlier's rates. The upsert is now conditional on the revision the caller
/// read, 0 meaning "create": a stale replace, or a create that finds the
/// document already there, conflicts instead.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class FeeScheduleRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-fs-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("fs_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private FeeScheduleRepositoryMongo Repo()
        => new(_db, new ConfigurationBuilder().Build(), NullLogger<FeeScheduleRepositoryMongo>.Instance);

    private const string ScheduleId = Tenant + ":plan-default:p1";

    /// <summary>What the plan-default PUT builds: a whole new schedule carrying the revision it read.</summary>
    private static FeeSchedule Schedule(decimal rate, long revision = 0) => new()
    {
        Id = ScheduleId,
        TenantId = Tenant,
        Name = "Plan default",
        Type = FeeScheduleType.Custom,
        DefaultForPlanId = "p1",
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Revision = revision,
        Lines =
        [
            new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = rate },
        ],
    };

    private static ProviderContract Contract(string feeScheduleId, long revision = 0) => new()
    {
        Id = ProviderContract.MakeId(Tenant, "1234567890", "p1"),
        TenantId = Tenant,
        ProviderNpi = "1234567890",
        PlanId = "p1",
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FeeScheduleId = feeScheduleId,
        Revision = revision,
    };

    private async Task<FeeSchedule> ReadAsync() => (await Repo().GetByIdAsync(Tenant, ScheduleId))!;

    private static decimal Rate(FeeSchedule s) => s.Lines.Single().Rate;

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task ACreate_StoresRevisionOne()
    {
        await Repo().UpsertAsync(Schedule(80m));

        var stored = await ReadAsync();
        Assert.Equal(1, stored.Revision);
        Assert.Equal(80m, Rate(stored));
    }

    [Fact]
    public async Task TwoConcurrentReplaces_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await Repo().UpsertAsync(Schedule(80m));
        var read = await ReadAsync();

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpsertAsync(Schedule(90m, read.Revision))),
            Try(() => Repo().UpsertAsync(Schedule(70m, read.Revision))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        var conflict = Assert.Single(outcomes.OfType<FeeScheduleConcurrencyException>());
        Assert.IsAssignableFrom<ConcurrencyConflictException>(conflict);
        var stored = await ReadAsync();
        Assert.Contains(Rate(stored), new[] { 90m, 70m });
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task TwoConcurrentCreates_ExactlyOneWins_TheOtherConflicts_InsteadOfOverwritingIt()
    {
        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpsertAsync(Schedule(90m))),
            Try(() => Repo().UpsertAsync(Schedule(70m))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Single(outcomes.OfType<FeeScheduleConcurrencyException>());
        Assert.Equal(1, (await ReadAsync()).Revision);
    }

    [Fact]
    public async Task AReplaceFromAStaleRead_Conflicts_AndLeavesTheNewerRatesInPlace()
    {
        await Repo().UpsertAsync(Schedule(80m));
        var stale = await ReadAsync();
        await Repo().UpsertAsync(Schedule(95m, stale.Revision));

        var write = Schedule(60m, stale.Revision);
        var thrown = await Assert.ThrowsAsync<FeeScheduleConcurrencyException>(() => Repo().UpsertAsync(write));

        Assert.Equal("Fee schedule", thrown.Entity);
        Assert.Equal(stale.Revision, write.Revision);
        var stored = await ReadAsync();
        Assert.Equal(95m, Rate(stored));
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task AScheduleWrittenBeforeTheRevisionExisted_IsReplacedOnce_ThenCarriesARevision()
    {
        await Repo().UpsertAsync(Schedule(80m));
        await _db.GetCollection<BsonDocument>("FeeSchedules").UpdateOneAsync(
            new BsonDocument("_id", ScheduleId), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync();
        Assert.Equal(0, legacy.Revision);

        await Repo().UpsertAsync(Schedule(85m, legacy.Revision));

        var stored = await ReadAsync();
        Assert.Equal(1, stored.Revision);
        Assert.Equal(85m, Rate(stored));
    }

    [Fact]
    public async Task AStaleProviderContractUpsert_Conflicts_AndLeavesTheNewerContractInPlace()
    {
        await Repo().UpsertAsync(Contract("FS-A"));
        await Repo().UpsertAsync(Contract("FS-B", revision: 1));

        var thrown = await Assert.ThrowsAsync<FeeScheduleConcurrencyException>(
            () => Repo().UpsertAsync(Contract("FS-STALE", revision: 1)));

        Assert.Equal("Provider contract", thrown.Entity);
        var stored = Assert.Single(await Repo().ListByProviderAsync(Tenant, "1234567890"));
        Assert.Equal("FS-B", stored.FeeScheduleId);
        Assert.Equal(2, stored.Revision);
    }
}
