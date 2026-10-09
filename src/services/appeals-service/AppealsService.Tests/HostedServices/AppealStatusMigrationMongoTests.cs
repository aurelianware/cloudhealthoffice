using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AppealsService.Tests.HostedServices;

/// <summary>
/// Runs <see cref="AppealStatusMigrationHostedService"/> against a real
/// mongod over rows written by <see cref="AppealRepositoryMongo"/>. The
/// repository stores the <see cref="Appeal"/> class map's PascalCase
/// element names (no camelCase convention is registered), so the migration
/// must use those names; it must still pick up rows an older writer stored
/// in camelCase.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AppealStatusMigrationMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly InMemoryAppealRepository _events = new();
    private readonly RecordingAppealEventPublisher _publisher = new();
    private readonly AppealRepositoryMongo _repo;
    private readonly IMongoCollection<BsonDocument> _raw;

    public AppealStatusMigrationMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("appeals_migration");
        _repo = new AppealRepositoryMongo(_db, _events);
        _raw = _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private AppealStatusMigrationHostedService NewMigration() => new(
        _db, _events, Config(), NullLogger<AppealStatusMigrationHostedService>.Instance);

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppealMigration:BatchSize"] = "2" // force several batches
        }).Build();

    /// <summary>One outbox relay sweep through <paramref name="transport"/> (default: the recording one).</summary>
    private Task<int> RelayAsync(RecordingAppealEventPublisher? transport = null,
        AppealOutboxOptions? options = null) =>
        (transport ?? _publisher).DispatcherFor(_repo, options).DispatchPendingAsync();

    private async Task<Appeal> CreateThroughRepositoryAsync(AppealStatus status = AppealStatus.Submitted)
    {
        var appeal = new Appeal
        {
            TenantId = "t1",
            Id = Guid.NewGuid().ToString(),
            AppealNumber = "APL-" + Guid.NewGuid().ToString("N")[..8],
            ClaimId = "c1",
            ClaimNumber = "CLM-001",
            MemberId = "m1",
            PatientName = "enc::patient",
            ProviderNPI = "1234567890",
            AppealReason = "enc::reason",
            LineOfBusiness = LineOfBusiness.Medicare,
            Status = status
        };
        await _repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = appeal.TenantId,
            AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated,
            ToStatus = status,
            ActorId = "user1"
        }.Queued(appeal));
        return appeal;
    }

    /// <summary>Stand in for a pre-modernization writer: overwrite the stored status with a legacy value.</summary>
    private Task SetStoredStatusAsync(Appeal appeal, string field, BsonValue value) =>
        _raw.UpdateOneAsync(
            new BsonDocument("_id", appeal.Id),
            new BsonDocument("$set", new BsonDocument(field, value)));

    [Fact]
    public async Task Repository_Stores_PascalCase_Element_Names()
    {
        var appeal = await CreateThroughRepositoryAsync();

        var doc = await _raw.Find(new BsonDocument("_id", appeal.Id)).SingleAsync();

        doc.Names.Should().Contain(new[] { "TenantId", "Status", "AppealNumber", "ClosureReasonCode" });
        doc.Names.Should().NotContain(new[] { "tenantId", "status", "appealNumber" },
            "no camelCase convention pack is registered for the Appeal class map");
        AppealStatusMigrationHostedService.Fields.Status.Current.Should().Be("Status");
    }

    [Fact]
    public async Task Previous_CamelCase_Filter_Matched_None_Of_The_Repository_Rows()
    {
        var appeal = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(appeal, "Status", "Approved");

        // The pre-fix filter: Filter.In("status", ...).
        (await _raw.CountDocumentsAsync(new BsonDocument("status",
                new BsonDocument("$in", new BsonArray { "Approved", "Denied", "PartialApproval", "Withdrawn" }))))
            .Should().Be(0);
        (await _raw.CountDocumentsAsync(AppealStatusMigrationHostedService.BuildLegacyStatusFilter()))
            .Should().Be(1);
    }

    [Fact]
    public async Task Migrates_Legacy_Rows_Written_Through_The_Repository()
    {
        var approved = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(approved, "Status", "Approved");
        var denied = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(denied, "Status", "denied");
        var withdrawn = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(withdrawn, "Status", 7); // old 0-indexed enum
        var partial = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(partial, "Status", "PartialApproval");
        var active = await CreateThroughRepositoryAsync(AppealStatus.InReview);

        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync();

        foreach (var (appeal, reason) in new[]
                 {
                     (approved, AppealClosureReasonCode.Approved),
                     (denied, AppealClosureReasonCode.Denied),
                     (withdrawn, AppealClosureReasonCode.Withdrawn),
                     (partial, AppealClosureReasonCode.PartialApproval)
                 })
        {
            var stored = (await _repo.GetByIdAsync("t1", appeal.Id))!;
            stored.Status.Should().Be(AppealStatus.Closed);
            stored.ClosureReasonCode.Should().Be(reason);
            stored.ClosedBy.Should().Be("system:migration");
            stored.ClosedAt.Should().NotBeNull();

            var audit = await _events.ListByAppealAsync("t1", appeal.Id);
            audit.Should().ContainSingle(e => e.EventType == AppealEventType.AppealStatusMigrated);
        }

        (await _repo.GetByIdAsync("t1", active.Id))!.Status.Should().Be(AppealStatus.InReview);
        _publisher.Migrated.Should().HaveCount(4);
        _publisher.Migrated.Should().OnlyContain(m => m.TenantId == "t1" && m.Actor == "system:migration");

        // Closed rows are found by the repository's own typed queries.
        (await _repo.SearchAsync("t1", new AppealSearchParams { Status = AppealStatus.Closed, PageSize = 100 }))
            .Should().HaveCount(4);
    }

    [Fact]
    public async Task Second_Run_Finds_Nothing()
    {
        var appeal = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(appeal, "Status", "Denied");

        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync();
        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync();

        _publisher.Migrated.Should().ContainSingle();
        (await _events.ListByAppealAsync("t1", appeal.Id))
            .Count(e => e.EventType == AppealEventType.AppealStatusMigrated).Should().Be(1);
    }

    [Fact]
    public async Task Ambiguous_Integer_Status_Under_The_Current_Name_Is_Left_Alone()
    {
        // 4 is legacy Approved under the old enum but PendingInfo under the current one.
        var appeal = await CreateThroughRepositoryAsync(AppealStatus.PendingInfo);
        await SetStoredStatusAsync(appeal, "Status", 4);

        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync();

        _publisher.Migrated.Should().BeEmpty();
        (await _raw.Find(new BsonDocument("_id", appeal.Id)).SingleAsync())["Status"].Should().Be(new BsonInt32(4));
    }

    [Fact]
    public async Task Migrates_A_Legacy_CamelCase_Row()
    {
        var id = Guid.NewGuid().ToString();
        await _raw.InsertOneAsync(new BsonDocument
        {
            { "_id", id },
            { "tenantId", "t1" },
            { "appealNumber", "APL-LEGACY-1" },
            { "claimId", "c9" },
            { "claimNumber", "CLM-9" },
            { "memberId", "m9" },
            { "providerNPI", "1234567890" },
            { "status", 5 } // old enum: Denied
        });

        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync();

        var call = _publisher.Migrated.Should().ContainSingle().Subject;
        call.AppealId.Should().Be(id);
        call.TenantId.Should().Be("t1");
        call.LegacyStatus.Should().Be("Denied");
        call.MappedReasonCode.Should().Be(AppealClosureReasonCode.Denied);

        var doc = await _raw.Find(new BsonDocument("_id", id)).SingleAsync();
        doc["status"].Should().Be(new BsonString("Closed"));
        doc["closureReasonCode"].Should().Be(new BsonString("Denied"));
        doc.Contains("Status").Should().BeTrue("the class-map field is written as well");

        // Idempotent for camelCase rows too.
        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync();
        _publisher.Migrated.Should().ContainSingle();
    }

    // ── Outbox: the migration no longer depends on the Kafka producer ───
    //
    // These replace the publisher-readiness tests from #1265. That wait
    // existed because a publish before the producer's StartAsync was
    // silently dropped; the event now commits with the status rewrite in
    // the appeal's outbox, so producer start order, failure or absence can
    // only delay it.

    private async Task<Appeal> SeedLegacyAsync()
    {
        var appeal = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(appeal, "Status", "Approved");
        return appeal;
    }

    [Fact]
    public async Task Migration_Writes_The_Event_To_The_Outbox_In_The_Same_Update()
    {
        var appeal = await SeedLegacyAsync();

        await NewMigration().StartAsync(CancellationToken.None);

        // Nothing published yet, but the event is durable on the row itself.
        _publisher.Migrated.Should().BeEmpty();
        var stored = (await _repo.GetByIdAsync("t1", appeal.Id))!;
        stored.Status.Should().Be(AppealStatus.Closed);
        var pending = stored.Outbox!.Where(m => m.EventType == AppealEventPublisher.AppealStatusMigratedType).ToList();
        pending.Should().ContainSingle().Which.Status.Should().Be(AppealOutboxStatus.Pending);

        var audit = (await _events.ListByAppealAsync("t1", appeal.Id))
            .Single(e => e.EventType == AppealEventType.AppealStatusMigrated);
        pending[0].IdempotencyKey.Should().Be(audit.EventId, "the audit row and the Kafka event share the idempotency key");
        pending[0].EventId.Should().Be(AppealOutbox.WireEventId("t1", appeal.Id, audit.EventId));

        await RelayAsync();
        var call = _publisher.Migrated.Should().ContainSingle().Subject;
        call.AppealId.Should().Be(appeal.Id);
        call.MappedReasonCode.Should().Be(AppealClosureReasonCode.Approved);
    }

    [Fact]
    public async Task Producer_That_Failed_To_Start_Does_Not_Block_Or_Lose_The_Migration_Event()
    {
        var appeal = await SeedLegacyAsync();
        var failed = new RecordingAppealEventPublisher(AppealEventPublisherState.Unavailable);

        await NewMigration().StartAsync(CancellationToken.None);
        (await RelayAsync(failed)).Should().Be(0);

        failed.Attempts.Should().Be(0, "an unavailable producer is never called");
        (await _repo.GetByIdAsync("t1", appeal.Id))!.Status.Should().Be(AppealStatus.Closed);
        (await _repo.GetByIdAsync("t1", appeal.Id))!.Outbox!
            .Should().ContainSingle(m => m.Status == AppealOutboxStatus.Pending
                                         && m.EventType == AppealEventPublisher.AppealStatusMigratedType);

        // The next start, with a working producer, publishes it (and the genesis event).
        (await RelayAsync()).Should().Be(2);
        _publisher.Migrated.Should().ContainSingle(c => c.AppealId == appeal.Id);
    }

    [Fact]
    public async Task Kafka_Disabled_Migrates_And_Keeps_The_Event_Pending_By_Default()
    {
        var appeal = await SeedLegacyAsync();
        var disabled = new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled);

        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync(disabled);

        disabled.Attempts.Should().Be(0);
        (await _events.ListByAppealAsync("t1", appeal.Id))
            .Should().ContainSingle(e => e.EventType == AppealEventType.AppealStatusMigrated, "audit rows are still written");
        (await _repo.GetByIdAsync("t1", appeal.Id))!.Outbox!
            .Single(m => m.EventType == AppealEventPublisher.AppealStatusMigratedType)
            .Status.Should().Be(AppealOutboxStatus.Pending);
    }

    [Fact]
    public async Task Kafka_Disabled_With_Skip_Marks_The_Event_Skipped()
    {
        var appeal = await SeedLegacyAsync();
        var disabled = new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled);

        await NewMigration().StartAsync(CancellationToken.None);
        await RelayAsync(disabled, new AppealOutboxOptions { Enabled = false, SkipWhenKafkaDisabled = true });

        disabled.Attempts.Should().Be(0);
        (await _repo.GetByIdAsync("t1", appeal.Id))!.Outbox!
            .Should().OnlyContain(m => m.Status == AppealOutboxStatus.Skipped && m.CompletedAt != null);
        (await _repo.FindDueAsync(DateTime.UtcNow, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_Registered_Before_The_Producer_Still_Delivers_The_Event()
    {
        // The registration order main had before #1265: the migration's
        // StartAsync ran before the producer's. It no longer matters.
        var appeal = await SeedLegacyAsync();
        var transport = new AppealEventPublisher(
            NullLogger<AppealEventPublisher>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = ""
            }).Build());
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(c => c.AddConfiguration(Config()))
            .ConfigureServices(s =>
            {
                s.AddSingleton(_db);
                s.AddSingleton<IAppealEventSink>(_events);
                s.AddHostedService<AppealStatusMigrationHostedService>();
                s.AddHostedService(_ => transport);
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        (await _repo.GetByIdAsync("t1", appeal.Id))!.Status.Should().Be(AppealStatus.Closed);
        await RelayAsync();
        _publisher.Migrated.Should().ContainSingle(c => c.AppealId == appeal.Id);
    }

    [Fact]
    public async Task Real_Publisher_Signals_Readiness_Only_From_StartAsync()
    {
        var publisher = new AppealEventPublisher(
            NullLogger<AppealEventPublisher>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = ""
            }).Build());

        publisher.Started.IsCompleted.Should().BeFalse();
        await publisher.StartAsync(CancellationToken.None);
        (await publisher.Started).Should().Be(AppealEventPublisherState.Disabled);
    }
}
