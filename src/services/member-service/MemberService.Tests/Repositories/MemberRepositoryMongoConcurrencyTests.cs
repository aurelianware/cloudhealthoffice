using CloudHealthOffice.Testing.Mongo;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MemberService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB member repository (EphemeralMongo).
/// <c>UpdateAsync</c> replaced the member unconditionally and ignored the
/// result, so of two concurrent edits (an address change and an identifier
/// add, say) the later silently erased the earlier. Every write now bumps
/// <see cref="Member.Revision"/> and a replace is conditional on the revision
/// it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MemberRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("member_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private MemberRepositoryMongo Repo() => new(_db);

    private async Task<Member> SeedAsync(string id) => await Repo().CreateAsync(new Member
    {
        Id = id,
        TenantId = Tenant,
        MemberId = "M-" + id,
        FirstName = "Pat",
        LastName = "Smith",
        GroupNumber = "G1",
        DateOfBirth = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    });

    private async Task<Member> ReadAsync(string id) => (await Repo().GetByIdAsync(Tenant, id))!;

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
        await SeedAsync("A1");
        var a = await ReadAsync("A1");
        var b = await ReadAsync("A1");
        a.Address = "1 Address Change St";
        b.Identifiers.Add(new MemberIdentifier { Type = MemberIdentifierType.MemberId, System = "urn:test", Value = "X-1" });

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is MemberConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<MemberConcurrencyException>().Single()
            .Should().BeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>("it is answered 409");
        (await ReadAsync("A1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task AStaleReplace_Conflicts_AndTheEarlierWriteSurvives_UntilReread()
    {
        await SeedAsync("A1");
        var stale = await ReadAsync("A1");
        var fresh = await ReadAsync("A1");
        fresh.Address = "1 Address Change St";
        await Repo().UpdateAsync(fresh);
        stale.Email = "pat@example.test";

        await ((Func<Task>)(() => Repo().UpdateAsync(stale))).Should().ThrowAsync<MemberConcurrencyException>();
        stale.Revision.Should().Be(0, "the in-memory revision is restored on a conflict");
        (await ReadAsync("A1")).Address.Should().Be("1 Address Change St");

        var reread = await ReadAsync("A1");
        reread.Email = "pat@example.test";
        await Repo().UpdateAsync(reread);

        var stored = await ReadAsync("A1");
        stored.Address.Should().Be("1 Address Change St");
        stored.Email.Should().Be("pat@example.test");
        stored.Revision.Should().Be(2);
    }

    [Fact]
    public async Task AMemberWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("A1");
        await _db.GetCollection<BsonDocument>("Members").UpdateOneAsync(
            new BsonDocument("_id", "A1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("A1");
        legacy.Revision.Should().Be(0);
        legacy.Phone = "555-0100";

        await Repo().UpdateAsync(legacy);

        (await ReadAsync("A1")).Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdatingAMissingMember_IsStillANoOp_NotAConflict()
    {
        var ghost = new Member { Id = "NOPE", TenantId = Tenant, MemberId = "M-NOPE" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync(Tenant, "NOPE")).Should().BeNull();
    }
}
