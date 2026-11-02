using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AppealsService.Tests.HostedServices;

/// <summary>
/// <see cref="AppealLineOfBusinessMigrationHostedService"/> against a real mongod: appeals
/// stored with the pre-consolidation values keep their meaning (a Part D case keeps its
/// Part D clocks) and read back through the repository on the shared line of business.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AppealLineOfBusinessMigrationMongoTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly AppealRepositoryMongo _repo;
    private readonly IMongoCollection<BsonDocument> _raw;

    public AppealLineOfBusinessMigrationMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("appeals_lob_migration");
        _repo = new AppealRepositoryMongo(_db, new InMemoryAppealRepository());
        _raw = _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private async Task<Appeal> StoredWithAsync(BsonValue legacyLineOfBusiness, string field = nameof(Appeal.LineOfBusiness))
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
            Status = AppealStatus.Submitted
        };
        await _repo.CreateAsync(appeal, new AppealEvent
        {
            TenantId = appeal.TenantId,
            AppealId = appeal.Id,
            EventId = Guid.NewGuid().ToString(),
            EventType = AppealEventType.AppealCreated,
            ToStatus = AppealStatus.Submitted,
            ActorId = "user1"
        }.Queued(appeal));
        // Stand in for a pre-consolidation writer.
        await _raw.UpdateOneAsync(new BsonDocument("_id", appeal.Id), new BsonDocument("$set", new BsonDocument(field, legacyLineOfBusiness)));
        return appeal;
    }

    private Task<BsonDocument> RawAsync(Appeal appeal) => _raw.Find(new BsonDocument("_id", appeal.Id)).SingleAsync();

    [Fact]
    public async Task Part_D_and_Marketplace_appeals_are_rewritten_and_keep_their_meaning()
    {
        var partD = await StoredWithAsync("MedicarePartD");
        var partDNumber = await StoredWithAsync(5); // the old appeals enum's number for Part D
        var marketplace = await StoredWithAsync("Marketplace");
        var medicaid = await StoredWithAsync("Medicaid");

        var (partDCount, exchangeCount) = await AppealLineOfBusinessMigrationHostedService.RunAsync(_raw, default);

        partDCount.Should().Be(2);
        exchangeCount.Should().Be(1);
        foreach (var a in new[] { partD, partDNumber })
        {
            var doc = await RawAsync(a);
            doc["LineOfBusiness"].AsString.Should().Be("Medicare");
            doc["BenefitType"].AsString.Should().Be("PartDDrug");
            (await _repo.GetByIdAsync("t1", a.Id))!.Coverage.Should().Be(AppealCoverage.PartD);
        }
        (await RawAsync(marketplace))["LineOfBusiness"].AsString.Should().Be("Exchange");
        (await _repo.GetByIdAsync("t1", marketplace.Id))!.LineOfBusiness.Should().Be(LineOfBusiness.Exchange);
        (await _repo.GetByIdAsync("t1", medicaid.Id))!.Coverage.Should().Be(new AppealCoverage(LineOfBusiness.Medicaid));
    }

    [Fact]
    public async Task A_camelCase_row_is_rewritten_and_a_second_run_changes_nothing()
    {
        var camel = await StoredWithAsync("MedicarePartD", field: "lineOfBusiness");

        await AppealLineOfBusinessMigrationHostedService.RunAsync(_raw, default);
        var second = await AppealLineOfBusinessMigrationHostedService.RunAsync(_raw, default);

        var doc = await RawAsync(camel);
        doc["lineOfBusiness"].AsString.Should().Be("Medicare");
        doc["benefitType"].AsString.Should().Be("PartDDrug");
        second.Should().Be((0L, 0L));
    }

    [Fact]
    public async Task The_hosted_service_runs_the_rewrite_on_start()
    {
        var partD = await StoredWithAsync("MedicarePartD");

        await new AppealLineOfBusinessMigrationHostedService(_db, NullLogger<AppealLineOfBusinessMigrationHostedService>.Instance)
            .StartAsync(default);

        (await _repo.GetByIdAsync("t1", partD.Id))!.Coverage.Should().Be(AppealCoverage.PartD);
    }
}
