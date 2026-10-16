using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using EncounterService.Models;
using EncounterService.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.EncounterService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB encounter repository, against a real
/// mongod. <c>UpdateAsync</c> replaced the encounter unconditionally, so of two
/// concurrent writes the later silently erased the earlier: a status update
/// read before a void turned a Voided encounter back into a live one, and two
/// batch dispatches could both claim the same Pending encounter. The replace
/// is now conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EncounterRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-enc-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enc_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    /// <summary>One request's context (not the AsyncLocal-backed HttpContextAccessor: these tests run requests at once).</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private EncounterRepositoryMongo Repo() => new(_db, Request(), Mock.Of<ILogger<EncounterRepositoryMongo>>());

    private Task<Encounter> SeedAsync(string id) => Repo().CreateAsync(new Encounter
    {
        Id = id,
        EncounterControlNumber = "ENC-" + id,
        ClaimId = "CLM-1",
        MemberId = "M1",
        PayerId = "PAYER-1",
        Status = EncounterStatus.Pending,
        TotalChargeAmount = 125m,
    });

    private async Task<Encounter> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

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
    public async Task TwoBatchDispatches_FromTheSameRead_OnlyOneClaimsTheEncounter()
    {
        await SeedAsync("e-1");
        var a = await ReadAsync("e-1");
        var b = await ReadAsync("e-1");
        a.BatchId = "BATCH-A";
        a.Status = EncounterStatus.Queued;
        b.BatchId = "BATCH-B";
        b.Status = EncounterStatus.Queued;

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one dispatch claims the encounter");
        outcomes.Count(o => o is EncounterConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<EncounterConcurrencyException>().Single().Should().BeAssignableTo<ConcurrencyConflictException>("409 at the API");
        var stored = await ReadAsync("e-1");
        stored.BatchId.Should().BeOneOf("BATCH-A", "BATCH-B");
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AStatusUpdateReadBeforeAVoid_Conflicts_InsteadOfUnvoidingTheEncounter()
    {
        await SeedAsync("e-1");
        var statusUpdate = await ReadAsync("e-1");
        var voiding = await ReadAsync("e-1");
        voiding.Status = EncounterStatus.Voided;
        await Repo().UpdateAsync(voiding);

        statusUpdate.Status = EncounterStatus.Accepted;
        var act = () => Repo().UpdateAsync(statusUpdate);

        await act.Should().ThrowAsync<EncounterConcurrencyException>();
        statusUpdate.Revision.Should().Be(0, "the in-memory revision is restored to the one read");
        var stored = await ReadAsync("e-1");
        stored.Status.Should().Be(EncounterStatus.Voided, "the void is not undone");
    }

    [Fact]
    public async Task AnEncounterWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("e-1");
        await _db.GetCollection<BsonDocument>("encounters").UpdateOneAsync(
            new BsonDocument("_id", "e-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("e-1");
        legacy.Revision.Should().Be(0);
        legacy.Status = EncounterStatus.Queued;

        await Repo().UpdateAsync(legacy);

        var stored = await ReadAsync("e-1");
        stored.Revision.Should().Be(1);
        stored.Status.Should().Be(EncounterStatus.Queued);
    }

    [Fact]
    public async Task UpdatingAMissingEncounter_IsStillANoOp_NotAConflict()
    {
        var ghost = new Encounter { Id = "NOPE", EncounterControlNumber = "ENC-NOPE" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync("NOPE")).Should().BeNull();
        ghost.Revision.Should().Be(0);
    }

    [Fact]
    public async Task ARereadAfterTheConflict_Succeeds()
    {
        await SeedAsync("e-1");
        var stale = await ReadAsync("e-1");
        var fresh = await ReadAsync("e-1");
        fresh.Status = EncounterStatus.Queued;
        await Repo().UpdateAsync(fresh);
        stale.Notes = "second";
        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<EncounterConcurrencyException>();

        var reread = await ReadAsync("e-1");
        reread.Notes = "second";
        await Repo().UpdateAsync(reread);

        var stored = await ReadAsync("e-1");
        stored.Notes.Should().Be("second");
        stored.Status.Should().Be(EncounterStatus.Queued);
        stored.Revision.Should().Be(2);
    }
}
