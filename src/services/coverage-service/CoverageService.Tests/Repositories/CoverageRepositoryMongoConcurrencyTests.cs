using CloudHealthOffice.Testing.Mongo;
using CoverageService.Exceptions;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB coverage repository (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the coverage unconditionally, so of two
/// concurrent edits the later silently erased the earlier, and an edit read
/// before <see cref="CoverageStatusSweepJob"/>'s conditional status write
/// turned a Terminated coverage back to Active. Every write now bumps
/// <see cref="Coverage.Revision"/> and a replace is conditional on the revision
/// it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CoverageRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("coverage_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private CoverageRepositoryMongo Repo() => new(_db);

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private async Task<Coverage> SeedAsync(string id, DateTime? termination = null) => await Repo().CreateAsync(new Coverage
    {
        Id = id,
        TenantId = Tenant,
        MemberId = "M1",
        GroupNumber = "G",
        PlanId = "P",
        Status = CoverageStatus.Active,
        EffectiveDate = D(2025, 1, 1),
        TerminationDate = termination,
    });

    private async Task<Coverage> ReadAsync(string id) => (await Repo().GetByIdAsync(Tenant, id))!;

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
    public async Task TwoConcurrentUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("C1");
        var a = await ReadAsync("C1");
        var b = await ReadAsync("C1");
        a.PlanId = "plan A";
        b.PlanId = "plan B";

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is CoverageConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        var stored = await ReadAsync("C1");
        stored.PlanId.Should().BeOneOf("plan A", "plan B");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AReplaceBasedOnAReadBeforeTheSweepTerminatedIt_Conflicts_InsteadOfUndoingTheTermination()
    {
        await SeedAsync("C1", termination: D(2025, 6, 30));
        var stale = await ReadAsync("C1");
        var swept = await ReadAsync("C1");

        (await Repo().SetStatusAsync(swept, CoverageStatus.Terminated, "coverage-status-sweep")).Should().BeTrue();
        stale.PlanId = "edited from a stale read";

        var act = () => Repo().UpdateAsync(stale);

        await act.Should().ThrowAsync<CoverageConcurrencyException>();
        var stored = await ReadAsync("C1");
        stored.Status.Should().Be(CoverageStatus.Terminated, "the sweep's termination is not undone");
        stored.PlanId.Should().Be("P");
        stored.Revision.Should().Be(1, "the sweep bumped the revision");
    }

    [Fact]
    public async Task ACoverageWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("C1");
        await _db.GetCollection<BsonDocument>("Coverage").UpdateOneAsync(
            new BsonDocument("_id", "C1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("C1");
        legacy.Revision.Should().Be(0);
        legacy.PlanId = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync("C1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdatingAMissingCoverage_IsStillNotFound_NotAConflict()
    {
        var ghost = new Coverage { Id = "NOPE", TenantId = Tenant };

        var act = () => Repo().UpdateAsync(ghost);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeOfType<CoverageConcurrencyException>();
        thrown.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task ARereadAfterTheConflict_Succeeds()
    {
        await SeedAsync("C1");
        var stale = await ReadAsync("C1");
        var fresh = await ReadAsync("C1");
        fresh.PlanId = "first";
        await Repo().UpdateAsync(fresh);
        stale.PlanId = "second";
        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<CoverageConcurrencyException>();

        var reread = await ReadAsync("C1");
        reread.PlanId = "second";
        await Repo().UpdateAsync(reread);

        (await ReadAsync("C1")).PlanId.Should().Be("second");
    }
}
