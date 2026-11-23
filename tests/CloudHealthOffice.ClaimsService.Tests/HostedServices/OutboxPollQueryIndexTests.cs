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
/// The outbox dispatchers poll the Claims collection on a timer, on every
/// claims-service replica, across every tenant. A poll query that cannot use
/// an index reads the whole collection, so its cost grows with every claim
/// ever stored: at 1M claims one such scan took about 1.2 s, and the
/// accumulator outbox's age query ran two of them per poll. That is a
/// steady-state slowdown that short benchmark runs never show.
///
/// Each poll query must read only the claims that hold a pending entry. These
/// tests seed many claims with no entry (both outbox slots stored as null, and
/// an emptied event list, as production leaves them) and a few with one, run
/// every poll query with the profiler on, and fail on any collection scan.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class OutboxPollQueryIndexTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const int ClaimsWithoutEntries = 50;
    private IMongoDatabase _db = null!;
    private ClaimRepositoryMongo _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("outbox_poll_query_index");
        _repo = new ClaimRepositoryMongo(_db, new HttpContextAccessor(), NullLogger<ClaimRepositoryMongo>.Instance);
        await new ClaimIndexInitializer(_db, NullLogger<ClaimIndexInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static AccumulatorOutboxItem Entry(string id, long createdAtMs) =>
        new() { Id = id, CreatedAtMs = createdAtMs, DueAtMs = createdAtMs };

    private Task SeedAsync(
        string id,
        AccumulatorOutboxItem? commit = null,
        AccumulatorOutboxItem? reversal = null,
        List<ClaimEventOutboxItem>? events = null) =>
        _db.GetCollection<Claim>("Claims").InsertOneAsync(new Claim
        {
            Id = id, ClaimVersionId = id, TenantId = "t1", ClaimNumber = "CN-" + id, VersionNumber = 1,
            PendingAccumulatorCommit = commit,
            PendingAccumulatorReversal = reversal,
            PendingClaimEvents = events ?? [],
        });

    private async Task SeedMixAsync()
    {
        for (var i = 0; i < ClaimsWithoutEntries; i++) await SeedAsync("done-" + i);
        await SeedAsync("commit-new", commit: Entry("c1", 3_000));
        await SeedAsync("commit-old", commit: Entry("c2", 1_000));
        await SeedAsync("reversal", reversal: Entry("r1", 2_000));
        await SeedAsync("event", events:
        [
            new ClaimEventOutboxItem { Id = "e1", Topic = "claims.finalized.v1", Key = "event", Payload = "{}", CreatedAtMs = 500, DueAtMs = 500 },
        ]);
    }

    [Fact]
    public async Task TheIndexBootstrap_IndexesTheAccumulatorEntriesCreatedAt()
    {
        var indexes = await (await _db.GetCollection<Claim>("Claims").Indexes.ListAsync()).ToListAsync();
        var byName = indexes.ToDictionary(x => x["name"].AsString);

        foreach (var (name, path) in new[]
                 {
                     ("accumulator_outbox_commit_created", "PendingAccumulatorCommit.CreatedAtMs"),
                     ("accumulator_outbox_reversal_created", "PendingAccumulatorReversal.CreatedAtMs"),
                 })
        {
            byName.Should().ContainKey(name);
            byName[name]["key"].AsBsonDocument.Should().BeEquivalentTo(new BsonDocument(path, 1));
            byName[name]["sparse"].AsBoolean.Should().BeTrue();
        }
    }

    [Fact]
    public async Task TheAccumulatorAgeQuery_ReturnsTheOldestEntry_AcrossBothSlots()
    {
        await SeedMixAsync();

        (await _repo.OldestAccumulatorOutboxAsync()).Should().Be(1_000);
    }

    [Fact]
    public async Task TheAccumulatorAgeQuery_IsEmpty_WhenNoEntryIsPending()
    {
        for (var i = 0; i < 3; i++) await SeedAsync("done-" + i);

        (await _repo.OldestAccumulatorOutboxAsync()).Should().BeNull();
    }

    [Fact]
    public async Task EveryPollQuery_ReadsThroughAnIndex_NotTheWholeCollection()
    {
        await SeedMixAsync();
        const long farFuture = long.MaxValue / 2;

        await _db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            (await _repo.FindDueAccumulatorOutboxAsync(farFuture, 50)).Should().HaveCount(3);
            (await _repo.OldestAccumulatorOutboxAsync()).Should().Be(1_000);
            (await _repo.FindDueClaimEventsAsync(farFuture, 100)).Should().ContainSingle();
            (await _repo.OldestClaimEventAsync()).Should().Be(500);
        }
        finally
        {
            await _db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }

        var entries = await _db.GetCollection<BsonDocument>("system.profile")
            .Find(new BsonDocument { { "ns", _db.DatabaseNamespace.DatabaseName + ".Claims" }, { "op", "query" } })
            .ToListAsync();

        // FindDueAccumulatorOutbox, two accumulator age queries, FindDueClaimEvents, OldestClaimEvent.
        entries.Should().HaveCount(5);
        foreach (var entry in entries)
        {
            var plan = entry["planSummary"].AsString;
            plan.Should().NotContain("COLLSCAN", "a poll query scanned the whole Claims collection: {0}", entry["command"].ToJson());
            plan.Should().Contain("IXSCAN");
            // Only claims holding an entry are read, never the 50 without one.
            entry["docsExamined"].ToInt64().Should().BeLessThanOrEqualTo(3,
                "a poll query read claims without a pending entry: {0}", entry["command"].ToJson());
        }
    }
}
