using ClaimsService.HostedServices;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.HostedServices;

/// <summary>
/// The claim-event outbox's age query (<see cref="IClaimRepository.OldestClaimEventAsync"/>)
/// runs on every dispatcher poll. It must read only the claims that hold a
/// pending event, through the index the bootstrap creates, not scan the whole
/// Claims collection. Azure DocumentDB sorts in memory (it does not push a
/// sort down to an index), so the part that must be indexed on every store is
/// the filter; on MongoDB the same index also gives the order.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimEventOutboxOldestQueryTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string IndexName = "claim_event_outbox_created";
    private IMongoDatabase _db = null!;
    private ClaimRepositoryMongo _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("claim_event_outbox_oldest");
        _repo = new ClaimRepositoryMongo(_db, new HttpContextAccessor(), NullLogger<ClaimRepositoryMongo>.Instance);
        await new ClaimIndexInitializer(_db, NullLogger<ClaimIndexInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private Task SeedAsync(string id, params long[] createdAtMs) => SeedAsync(id, createdAtMs.Length > 0, createdAtMs);

    private Task SeedAsync(string id, bool hasList, params long[] createdAtMs) =>
        _db.GetCollection<Claim>("Claims").InsertOneAsync(new Claim
        {
            Id = id, ClaimVersionId = id, TenantId = "t1", ClaimNumber = "CN-" + id, VersionNumber = 1,
            PendingClaimEvents = !hasList
                ? null
                : createdAtMs.Select((ms, i) => new ClaimEventOutboxItem
                {
                    Id = $"{id}-e{i}", Topic = "claims.finalized.v1", Key = id, Payload = "{}",
                    CreatedAtMs = ms, DueAtMs = ms,
                }).ToList(),
        });

    [Fact]
    public async Task TheIndexBootstrap_IndexesThePendingEventsCreatedAt()
    {
        var indexes = await (await _db.GetCollection<Claim>("Claims").Indexes.ListAsync()).ToListAsync();
        var byName = indexes.ToDictionary(x => x["name"].AsString);

        byName.Should().ContainKey(IndexName);
        byName[IndexName]["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument("PendingClaimEvents.CreatedAtMs", 1));
        byName[IndexName]["sparse"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task TheOldestQuery_ReturnsTheSmallestCreatedAt_AcrossClaimsAndWithinAClaim()
    {
        await SeedAsync("none");
        await SeedAsync("A", 5_000, 1_500); // a claim's events are not necessarily stored oldest first
        await SeedAsync("B", 2_000);
        await SeedAsync("emptied", hasList: true); // every event published: $pull leaves []

        (await _repo.OldestClaimEventAsync()).Should().Be(1_500);
    }

    [Fact]
    public async Task TheOldestQuery_IsEmpty_WhenNothingIsPending()
    {
        await SeedAsync("none");
        await SeedAsync("emptied", hasList: true);

        (await _repo.OldestClaimEventAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TheOldestQuery_ReadsThroughTheIndex_NotTheWholeCollection()
    {
        for (var i = 0; i < 50; i++) await SeedAsync("none-" + i);
        await SeedAsync("A", 3_000);
        await SeedAsync("B", 1_000);

        // The profiler records the plan the server actually ran for the repository's query.
        await _db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            (await _repo.OldestClaimEventAsync()).Should().Be(1_000);
        }
        finally
        {
            await _db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }

        var entry = await _db.GetCollection<BsonDocument>("system.profile")
            .Find(new BsonDocument { { "ns", _db.DatabaseNamespace.DatabaseName + ".Claims" }, { "op", "query" } })
            .Sort(new BsonDocument("ts", -1)).FirstAsync();
        entry["planSummary"].AsString.Should().Contain("IXSCAN").And.Contain("PendingClaimEvents.CreatedAtMs")
            .And.NotContain("COLLSCAN");
        // MongoDB takes the order from the multikey index too (no SORT stage), so the
        // limit stops at the first claim. DocumentDB would read both pending claims and
        // sort them in memory; neither reads the 50 claims without an event.
        entry["docsExamined"].ToInt64().Should().Be(1);
        entry["execStats"].ToJson().Should().NotContain("\"SORT\"");
    }
}
