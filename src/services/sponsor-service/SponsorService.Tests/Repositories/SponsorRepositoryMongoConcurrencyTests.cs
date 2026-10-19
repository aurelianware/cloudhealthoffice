using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using SponsorService.Models;
using SponsorService.Repositories;
using SponsorService.Services;

namespace SponsorService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB sponsor repository (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the sponsor unconditionally, so an edit based
/// on a read from before a status change (premium billing suspending a
/// delinquent sponsor) put the old status back, and two edits erased each
/// other. Every write now bumps <see cref="Sponsor.Revision"/>, the status
/// change included, and a replace is conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class SponsorRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("sponsor_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private SponsorRepositoryMongo Repo() => new(
        _db, new ConfigurationBuilder().Build(), NullLogger<SponsorRepositoryMongo>.Instance);

    private async Task SeedAsync(string id) => await Repo().CreateAsync(new Sponsor
    {
        Id = id,
        TenantId = Tenant,
        GroupNumber = "G-" + id,
        EmployerName = "Acme",
        Status = SponsorStatus.Active,
    });

    private async Task<Sponsor> ReadAsync(string id) => (await Repo().GetByIdAsync(Tenant, id))!;

    private static SponsorStatusChange Suspend() => new()
    {
        From = SponsorStatus.Active,
        To = SponsorStatus.Suspended,
        Reason = "delinquent",
        ChangedBy = "premium-billing",
        ChangedByIsService = true,
        ChangedAt = DateTime.UtcNow,
    };

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
    public async Task AnEditBasedOnAReadFromBeforeAStatusChange_Conflicts_AndTheSuspensionSurvives()
    {
        await SeedAsync("S1");
        var stale = await ReadAsync("S1");

        (await Repo().UpdateStatusAsync(Tenant, "S1", Suspend())).Should().BeTrue();
        stale.ContactName = "Pat";

        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<SponsorConcurrencyException>()
            .Where(ex => ex is CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException, "it is answered 409");
        stale.Revision.Should().Be(0, "the in-memory revision is restored on a conflict");

        var stored = await ReadAsync("S1");
        stored.Status.Should().Be(SponsorStatus.Suspended, "the stale edit must not put Active back");
        stored.StatusHistory.Should().ContainSingle();
        stored.Revision.Should().Be(1);

        var reread = await ReadAsync("S1");
        reread.ContactName = "Pat";
        await Repo().UpdateAsync(reread);

        stored = await ReadAsync("S1");
        stored.Status.Should().Be(SponsorStatus.Suspended);
        stored.ContactName.Should().Be("Pat");
        stored.Revision.Should().Be(2);
    }

    [Fact]
    public async Task TwoConcurrentEdits_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("S1");
        var a = await ReadAsync("S1");
        var b = await ReadAsync("S1");
        a.ContactName = "Pat";
        b.ContactEmail = "billing@acme.test";

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is SponsorConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        (await ReadAsync("S1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task ASponsorWrittenBeforeTheRevisionExisted_UpdatesAndChangesStatus_ThenCarriesARevision()
    {
        await SeedAsync("S1");
        await SeedAsync("S2");
        var raw = _db.GetCollection<BsonDocument>("Sponsors");
        await raw.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$unset", new BsonDocument("Revision", "")));

        var legacy = await ReadAsync("S1");
        legacy.Revision.Should().Be(0);
        legacy.ContactName = "Pat";
        await Repo().UpdateAsync(legacy);
        (await ReadAsync("S1")).Revision.Should().Be(1);

        (await Repo().UpdateStatusAsync(Tenant, "S2", Suspend())).Should().BeTrue();
        (await ReadAsync("S2")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdatingAMissingSponsor_IsStillANoOp_NotAConflict()
    {
        var ghost = new Sponsor { Id = "NOPE", TenantId = Tenant, GroupNumber = "G-NOPE", EmployerName = "Ghost" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync(Tenant, "NOPE")).Should().BeNull();
        (await Repo().UpdateStatusAsync(Tenant, "NOPE", Suspend())).Should().BeFalse();
    }
}
