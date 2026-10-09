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

    private AppealStatusMigrationHostedService NewMigration()
    {
        var started = new GatedAppealEventPublisher();
        started.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return NewMigration(_publisher, started);
    }

    private AppealStatusMigrationHostedService NewMigration(
        IAppealEventPublisher publisher, IAppealEventPublisherReadiness readiness, string readyTimeoutSeconds = "30") => new(
        _db, _events, publisher, readiness,
        Config(readyTimeoutSeconds),
        NullLogger<AppealStatusMigrationHostedService>.Instance);

    private static IConfiguration Config(string readyTimeoutSeconds = "30") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppealMigration:BatchSize"] = "2", // force several batches
            ["AppealMigration:PublisherReadyTimeoutSeconds"] = readyTimeoutSeconds
        }).Build();

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
        });
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
        await NewMigration().StartAsync(CancellationToken.None);

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
        _publisher.Migrated.Should().ContainSingle();
    }

    // ── Publisher readiness: no dependence on hosted-service order ──────

    /// <summary>
    /// A host wired like Program.cs's Mongo branch, with the migration and
    /// the publisher registered as hosted services in the given order.
    /// Hosted services start sequentially in registration order.
    /// </summary>
    private IHost BuildHost(GatedAppealEventPublisher publisher, bool migrationFirst, string readyTimeoutSeconds = "30") =>
        new HostBuilder()
            .ConfigureAppConfiguration(c => c.AddConfiguration(Config(readyTimeoutSeconds)))
            .ConfigureServices(s =>
            {
                s.AddSingleton(_db);
                s.AddSingleton<IAppealEventSink>(_events);
                s.AddSingleton(publisher);
                s.AddSingleton<IAppealEventPublisher>(publisher);
                s.AddSingleton<IAppealEventPublisherReadiness>(publisher);
                if (migrationFirst) s.AddHostedService<AppealStatusMigrationHostedService>();
                s.AddHostedService(sp => sp.GetRequiredService<GatedAppealEventPublisher>());
                if (!migrationFirst) s.AddHostedService<AppealStatusMigrationHostedService>();
            })
            .Build();

    private static AppealStatusMigrationHostedService MigrationOf(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<AppealStatusMigrationHostedService>().Single();

    private async Task<Appeal> SeedLegacyAsync()
    {
        var appeal = await CreateThroughRepositoryAsync();
        await SetStoredStatusAsync(appeal, "Status", "Approved");
        return appeal;
    }

    [Fact]
    public async Task Migration_Registered_Before_The_Publisher_Still_Produces_The_Event()
    {
        // The order main had: migration's StartAsync ran before the
        // publisher's, so every publish was skipped.
        var appeal = await SeedLegacyAsync();
        var publisher = new GatedAppealEventPublisher();
        using var host = BuildHost(publisher, migrationFirst: true);

        await host.StartAsync();
        var migration = MigrationOf(host);
        migration.DeferredRun.Should().NotBeNull("the publisher had not started yet, so the run waits for it");
        await migration.DeferredRun!;
        await host.StopAsync();

        publisher.DroppedBeforeStart.Should().Be(0);
        var call = publisher.Inner.Migrated.Should().ContainSingle().Subject;
        call.AppealId.Should().Be(appeal.Id);
        call.MappedReasonCode.Should().Be(AppealClosureReasonCode.Approved);
        (await _repo.GetByIdAsync("t1", appeal.Id))!.Status.Should().Be(AppealStatus.Closed);
    }

    [Fact]
    public async Task Publisher_Registered_First_Runs_The_Migration_During_Startup()
    {
        var appeal = await SeedLegacyAsync();
        var publisher = new GatedAppealEventPublisher();
        using var host = BuildHost(publisher, migrationFirst: false);

        await host.StartAsync();
        MigrationOf(host).DeferredRun.Should().BeNull();
        await host.StopAsync();

        publisher.DroppedBeforeStart.Should().Be(0);
        publisher.Inner.Migrated.Should().ContainSingle(c => c.AppealId == appeal.Id);
    }

    [Fact]
    public async Task Publisher_That_Never_Starts_Leaves_Rows_For_The_Next_Start()
    {
        var appeal = await SeedLegacyAsync();
        var stuck = new GatedAppealEventPublisher { NeverStarts = true };
        using (var host = BuildHost(stuck, migrationFirst: true, readyTimeoutSeconds: "0.5"))
        {
            await host.StartAsync();
            await MigrationOf(host).DeferredRun!;
            await host.StopAsync();
        }

        // Nothing migrated, so nothing was lost: no audit row, no publish
        // attempt, and the row is still legacy.
        stuck.DroppedBeforeStart.Should().Be(0);
        stuck.Inner.Migrated.Should().BeEmpty();
        (await _events.ListByAppealAsync("t1", appeal.Id))
            .Should().NotContain(e => e.EventType == AppealEventType.AppealStatusMigrated);
        (await _raw.Find(new BsonDocument("_id", appeal.Id)).SingleAsync())["Status"]
            .Should().Be(new BsonString("Approved"));

        // The next start, with a working publisher, migrates and publishes it.
        var healthy = new GatedAppealEventPublisher();
        using (var host = BuildHost(healthy, migrationFirst: false))
        {
            await host.StartAsync();
            await host.StopAsync();
        }
        healthy.Inner.Migrated.Should().ContainSingle(c => c.AppealId == appeal.Id);
        (await _repo.GetByIdAsync("t1", appeal.Id))!.Status.Should().Be(AppealStatus.Closed);
    }

    [Fact]
    public async Task Publisher_That_Fails_To_Start_Leaves_Rows_Unmigrated()
    {
        var appeal = await SeedLegacyAsync();
        var failed = new GatedAppealEventPublisher { StartState = AppealEventPublisherState.Unavailable };
        using var host = BuildHost(failed, migrationFirst: false);

        await host.StartAsync();
        await host.StopAsync();

        failed.DroppedBeforeStart.Should().Be(0);
        failed.Inner.Migrated.Should().BeEmpty();
        (await _raw.Find(new BsonDocument("_id", appeal.Id)).SingleAsync())["Status"]
            .Should().Be(new BsonString("Approved"));
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
