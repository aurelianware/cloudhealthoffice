using System.Diagnostics.Metrics;
using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AppealsService.Tests.Outbox;

/// <summary>
/// <see cref="AppealOutboxStoreScenarios{TRepo}"/> against a real, standalone
/// mongod (no replica set — the deployment shape the outbox is designed for),
/// plus the Mongo-only checks: the stored BSON shape and the sweep's index.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AppealOutboxMongoTests : AppealOutboxStoreScenarios<AppealRepositoryMongo>
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public AppealOutboxMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("appeals_outbox");
        _repo = new AppealRepositoryMongo(_db, _audit);
    }

    public override Task InitializeAsync() =>
        new AppealIndexInitializer(_db, NullLogger<AppealIndexInitializer>.Instance).StartAsync(CancellationToken.None);

    public override Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    protected override async Task AssertStoredDocumentAsync(string appealId)
    {
        var raw = await _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName)
            .Find(new BsonDocument("_id", appealId)).SingleAsync();
        raw["Outbox"].AsBsonArray.Should().HaveCount(3);
        raw["Outbox"][0]["Status"].Should().Be(new BsonString("Pending"));
        raw.Contains("OutboxNextDueAt").Should().BeFalse("the Cosmos sweep fields are not stored in Mongo");
    }

    protected override Task AppendLegacyTwinAsync(string appealId, AppealOutboxMessage first)
    {
        var twin = new BsonDocument
        {
            { "_id", Guid.NewGuid().ToString("N") }, { "IdempotencyKey", first.IdempotencyKey }, // class map: Id -> _id
            { "EventId", first.EventId }, { "EventType", first.EventType }, { "TenantId", Tenant },
            { "AppealId", appealId }, { "PayloadJson", first.PayloadJson }, { "CreatedAt", DateTime.UtcNow },
            { "Status", "Pending" }, { "Attempts", 0 }
        };
        return _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName)
            .UpdateOneAsync(new BsonDocument("_id", appealId), new BsonDocument("$push", new BsonDocument("Outbox", twin)));
    }

    // ── MAJOR: sweep uses the partial index ─────────────────────────────

    [Fact]
    public async Task Sweep_Query_Uses_The_Pending_Partial_Index()
    {
        for (var i = 0; i < 5; i++) await CreateAsync();

        var collection = _db.GetCollection<Appeal>(AppealRepositoryMongo.AppealsCollectionName);
        var rendered = AppealRepositoryMongo.DueFilter(Now).Render(
            new RenderArgs<Appeal>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry));
        var explain = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", new BsonDocument { { "find", AppealRepositoryMongo.AppealsCollectionName }, { "filter", rendered } } },
            { "verbosity", "queryPlanner" }
        });

        var plan = explain["queryPlanner"]["winningPlan"].ToJson();
        plan.Should().Contain("IXSCAN").And.Contain("ix_outbox_pending");
        plan.Should().NotContain("COLLSCAN");
    }
}
