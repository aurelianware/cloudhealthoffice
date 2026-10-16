using CloudHealthOffice.Testing.Mongo;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MemberService.Tests.Repositories;

/// <summary>A mongod started as a single-node replica set, so multi-document transactions work.</summary>
public sealed class FamilyRelationshipReplicaSetFixture : IDisposable
{
    private readonly EphemeralMongo.IMongoRunner _runner;

    public FamilyRelationshipReplicaSetFixture()
    {
        var options = TestMongo.Options();
        options.UseSingleNodeReplicaSet = true;
        _runner = EphemeralMongo.MongoRunner.Run(options);
        Client = new MongoClient(_runner.ConnectionString);
    }

    public IMongoClient Client { get; }

    public void Dispose() => _runner.Dispose();
}

/// <summary>
/// Optimistic concurrency on the MongoDB family-relationship pair update
/// (EphemeralMongo, replica set). <c>UpdatePairAsync</c> wrote both rows in a
/// transaction, but replaced each unconditionally, so a pair update based on
/// a stale read (an edit read before an end or a soft-delete) silently undid
/// the other write. Each row is now replaced only while it still has the
/// <see cref="FamilyRelationship.Revision"/> it was read with; otherwise the
/// transaction aborts, neither row is written, and the caller gets a conflict.
/// </summary>
public sealed class FamilyRelationshipRepositoryMongoConcurrencyTests(FamilyRelationshipReplicaSetFixture mongo)
    : IClassFixture<FamilyRelationshipReplicaSetFixture>, IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"member_family_rs_{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    private FamilyRelationshipRepositoryMongo Repo() => new(mongo.Client, _db);

    private IMongoCollection<BsonDocument> Raw() =>
        _db.GetCollection<BsonDocument>(FamilyRelationshipRepositoryMongo.CollectionName);

    private async Task SeedPairAsync(string pairId)
    {
        await Repo().CreatePairAsync(
            new FamilyRelationship
            {
                Id = pairId + "-F", TenantId = Tenant, PairId = pairId,
                SubjectMemberId = "SUB", RelatedMemberId = "DEP", RelationshipCode = "19",
                StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new FamilyRelationship
            {
                Id = pairId + "-I", TenantId = Tenant, PairId = pairId,
                SubjectMemberId = "DEP", RelatedMemberId = "SUB", RelationshipCode = "G8",
                StartDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
    }

    private async Task<(FamilyRelationship Forward, FamilyRelationship Inverse)> ReadPairAsync(string pairId)
    {
        var rows = await Repo().GetPairAsync(Tenant, pairId);
        return (rows.Single(r => r.Id == pairId + "-F"), rows.Single(r => r.Id == pairId + "-I"));
    }

    private static readonly DateTime EndDate = new(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc);

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
    public async Task AStalePairUpdate_Aborts_AndTheEarlierUpdateSurvives_UntilReread()
    {
        await SeedPairAsync("P1");
        var stale = await ReadPairAsync("P1");
        var fresh = await ReadPairAsync("P1");
        fresh.Forward.EndDate = EndDate;
        fresh.Inverse.EndDate = EndDate;
        await Repo().UpdatePairAsync(fresh.Forward, fresh.Inverse);

        stale.Forward.IsCustodial = true;
        stale.Inverse.IsCustodial = true;
        var thrown = await ((Func<Task>)(() => Repo().UpdatePairAsync(stale.Forward, stale.Inverse)))
            .Should().ThrowAsync<FamilyRelationshipConcurrencyException>();
        thrown.Which.Should().BeAssignableTo<CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException>("it is answered 409");
        stale.Forward.Revision.Should().Be(0, "the in-memory revisions are restored on a conflict");
        stale.Inverse.Revision.Should().Be(0);

        var stored = await ReadPairAsync("P1");
        stored.Forward.EndDate.Should().Be(EndDate, "the stale update must not clear the end date");
        stored.Inverse.EndDate.Should().Be(EndDate);
        stored.Forward.IsCustodial.Should().BeFalse();

        var reread = await ReadPairAsync("P1");
        reread.Forward.IsCustodial = true;
        reread.Inverse.IsCustodial = true;
        await Repo().UpdatePairAsync(reread.Forward, reread.Inverse);

        stored = await ReadPairAsync("P1");
        stored.Forward.EndDate.Should().Be(EndDate);
        stored.Forward.IsCustodial.Should().BeTrue();
        stored.Inverse.IsCustodial.Should().BeTrue();
        stored.Forward.Revision.Should().Be(2);
        stored.Inverse.Revision.Should().Be(2);
    }

    [Fact]
    public async Task OnlyTheInverseRowIsStale_TheTransactionAborts_AndTheForwardRowIsNotWritten()
    {
        await SeedPairAsync("P1");
        var (forward, inverse) = await ReadPairAsync("P1");
        // Another writer changed only the inverse row since it was read.
        await Raw().UpdateOneAsync(new BsonDocument("_id", "P1-I"),
            new BsonDocument
            {
                { "$set", new BsonDocument("QmcsoReference", "QMCSO-1") },
                { "$inc", new BsonDocument("Revision", 1L) },
            });

        forward.IsCustodial = true;
        inverse.IsCustodial = true;
        await ((Func<Task>)(() => Repo().UpdatePairAsync(forward, inverse)))
            .Should().ThrowAsync<FamilyRelationshipConcurrencyException>();

        var stored = await ReadPairAsync("P1");
        stored.Forward.IsCustodial.Should().BeFalse("the forward row's write is rolled back with the transaction");
        stored.Forward.Revision.Should().Be(0);
        stored.Inverse.QmcsoReference.Should().Be("QMCSO-1");
        stored.Inverse.IsCustodial.Should().BeFalse();
    }

    [Fact]
    public async Task TwoConcurrentPairUpdates_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedPairAsync("P1");
        var a = await ReadPairAsync("P1");
        var b = await ReadPairAsync("P1");
        a.Forward.EndDate = EndDate;
        a.Inverse.EndDate = EndDate;
        b.Forward.DeletedAt = DateTime.UtcNow;
        b.Inverse.DeletedAt = DateTime.UtcNow;

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdatePairAsync(a.Forward, a.Inverse)),
            Try(() => Repo().UpdatePairAsync(b.Forward, b.Inverse)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one update wins");
        outcomes.Count(o => o is FamilyRelationshipConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        var stored = await ReadPairAsync("P1");
        stored.Forward.Revision.Should().Be(1);
        stored.Inverse.Revision.Should().Be(1);
        (stored.Forward.EndDate == null).Should().Be(stored.Inverse.EndDate == null, "the pair stays symmetric");
    }

    [Fact]
    public async Task APairWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedPairAsync("P1");
        await Raw().UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var (forward, inverse) = await ReadPairAsync("P1");
        forward.Revision.Should().Be(0);
        forward.EndDate = EndDate;
        inverse.EndDate = EndDate;

        await Repo().UpdatePairAsync(forward, inverse);

        var stored = await ReadPairAsync("P1");
        stored.Forward.Revision.Should().Be(1);
        stored.Inverse.Revision.Should().Be(1);
        stored.Forward.EndDate.Should().Be(EndDate);
    }

    [Fact]
    public async Task UpdatingAMissingPair_IsStillANoOp_NotAConflict()
    {
        var forward = new FamilyRelationship { Id = "NOPE-F", TenantId = Tenant, PairId = "NOPE" };
        var inverse = new FamilyRelationship { Id = "NOPE-I", TenantId = Tenant, PairId = "NOPE" };

        await Repo().UpdatePairAsync(forward, inverse);

        (await Repo().GetPairAsync(Tenant, "NOPE")).Should().BeEmpty();
    }
}
