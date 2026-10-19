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
        raw.Contains("OutboxNextDueAt").Should().BeFalse("no top-level sweep fields are stored");
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

    // ── Ported from the Cosmos outbox suites (emulator and mocked container) ──

    /// <summary>A lease write racing an appeal change: neither is lost.</summary>
    [Fact]
    public async Task Concurrent_Appeal_Change_And_Lease_Writes_Both_Survive()
    {
        var appeal = await CreateAsync();
        var lease = Task.Run(() => _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)));
        var note = Task.Run(() => AddNoteAsync(appeal));
        await Task.WhenAll(lease, note);

        var stored = await StoredAsync(appeal.Id);
        stored.Notes.Should().ContainSingle();
        stored.Outbox!.Should().HaveCount(2);
        if (lease.Result is not null) stored.OutboxLeaseOwner.Should().Be("pod-a");
    }

    /// <summary>The sweep sees every tenant, skips leased appeals until the lease lapses, and honours its limit.</summary>
    [Fact]
    public async Task Sweep_Finds_Due_Appeals_Across_Tenants_But_Not_Leased_Ones()
    {
        var a = await CreateAsync();
        var b = await CreateAsync("t-other");
        var leased = await CreateAsync();
        (await _repo.TryLeaseAsync(Tenant, leased.Id, "pod-a", Now, Now.AddSeconds(30))).Should().NotBeNull();

        var due = await _repo.FindDueAsync(Now.AddSeconds(1), 10);

        due.Should().BeEquivalentTo(new[] { new AppealOutboxKey(Tenant, a.Id), new AppealOutboxKey("t-other", b.Id) });
        (await _repo.FindDueAsync(Now.AddSeconds(1), 1)).Should().HaveCount(1, "the limit binds");
        (await _repo.FindDueAsync(Now.AddSeconds(31), 10)).Should().HaveCount(3, "an expired lease is due again");
    }

    /// <summary>An outcome is recorded only by the lease holder, on the row with that id, while it is still pending.</summary>
    [Fact]
    public async Task Outcome_Update_Needs_The_Lease_Holder_And_A_Pending_Row()
    {
        var appeal = await CreateAsync();
        var lease = (await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)))!;
        var entry = lease.Entries.Single();
        entry.Status = AppealOutboxStatus.Sent;
        entry.CompletedAt = Now;

        (await _repo.UpdateMessageAsync(Tenant, appeal.Id, "pod-b", entry)).Should().BeFalse("not the lease holder");
        (await _repo.UpdateMessageAsync(Tenant, appeal.Id, "pod-a", entry)).Should().BeTrue();
        (await _repo.UpdateMessageAsync(Tenant, appeal.Id, "pod-a", entry)).Should().BeFalse("no longer pending");

        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Sent);
    }

    /// <summary>An appeal whose only entry is backing off is not due, so no lease is taken on it.</summary>
    [Fact]
    public async Task Lease_Is_Not_Taken_While_The_Only_Entry_Is_Backing_Off()
    {
        var appeal = await CreateAsync();
        var entry = (await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)))!.Entries.Single();
        entry.Attempts = 1;
        entry.NextAttemptAt = Now.AddMinutes(5);
        (await _repo.UpdateMessageAsync(Tenant, appeal.Id, "pod-a", entry)).Should().BeTrue();
        await _repo.ReleaseLeaseAsync(Tenant, appeal.Id, "pod-a", Array.Empty<string>());

        (await _repo.FindDueAsync(Now.AddMinutes(1), 10)).Should().BeEmpty();
        (await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now.AddMinutes(1), Now.AddMinutes(2))).Should().BeNull();
        (await StoredAsync(appeal.Id)).OutboxLeaseOwner.Should().BeNull("no lease write when nothing is due");
        (await _repo.FindDueAsync(Now.AddMinutes(6), 10)).Should().ContainSingle();
    }

    /// <summary>A publish sequence is assigned only when the appeal's counter is at the previous value.</summary>
    [Fact]
    public async Task Sequence_Assignment_Is_Conditional_On_The_Counter()
    {
        var appeal = await CreateAsync();
        var entry = (await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)))!.Entries.Single();

        (await _repo.RenewLeaseAsync(Tenant, appeal.Id, "pod-a", Now.AddSeconds(30),
            new AppealOutboxSequenceAssignment(entry.Id, 2))).Should().BeFalse("the counter is at 0, not 1");
        (await _repo.RenewLeaseAsync(Tenant, appeal.Id, "pod-a", Now.AddSeconds(30),
            new AppealOutboxSequenceAssignment(entry.Id, 1))).Should().BeTrue();

        var stored = await StoredAsync(appeal.Id);
        stored.OutboxSequence.Should().Be(1);
        stored.Outbox!.Single().Sequence.Should().Be(1);
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
