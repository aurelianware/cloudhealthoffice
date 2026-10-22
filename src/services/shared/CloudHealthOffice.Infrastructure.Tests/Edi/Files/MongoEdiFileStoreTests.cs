using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Tests.Gateways.Persistence;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.Files;

/// <summary>The file store over real MongoDB: tenant isolation, filters, newest first, and the indexes.</summary>
public sealed class MongoEdiFileStoreTests(MongoClaimTransmissionTenantTests.Mongod mongo)
    : IClassFixture<MongoClaimTransmissionTenantTests.Mongod>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private MongoEdiFileStore _store = null!;
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"edi_files_{Guid.NewGuid():N}");
        _store = new MongoEdiFileStore(_db);
        await _store.CreateAsync(File("t1", "834", EdiFileStatus.Applied, T0, "000000001", "11", "0001"));
        await _store.CreateAsync(File("t1", "837", EdiFileStatus.Rejected, T0.AddHours(1), "000000002", "12", "0002"));
        await _store.CreateAsync(File("t1", "837", EdiFileStatus.Duplicate, T0.AddHours(2), "000000002", "12", "0002"));
        await _store.CreateAsync(File("t2", "837", EdiFileStatus.Applied, T0.AddHours(3), "000000002", "12", "0002"));
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    private static EdiFile File(string tenant, string type, string status, DateTime at, string isa13, string gs06, string st02) => new()
    {
        TenantId = tenant, TransactionType = type, Status = status, ReceivedAt = at, UpdatedAt = at,
        InterchangeControlNumbers = [isa13], GroupControlNumbers = [gs06], TransactionSetControlNumbers = [st02],
        History = [new EdiFileStatusChange { Status = status, At = at }],
    };

    [Fact]
    public async Task ListsTheTenantsFilesNewestFirst()
    {
        var files = await _store.ListAsync("t1", new EdiFileQuery());
        files.Select(f => f.ReceivedAt).Should().BeInDescendingOrder();
        files.Should().HaveCount(3).And.OnlyContain(f => f.TenantId == "t1");
        (await _store.ListAsync("t3", new EdiFileQuery())).Should().BeEmpty();
    }

    [Fact]
    public async Task Filters()
    {
        (await _store.ListAsync("t1", new EdiFileQuery { TransactionType = "837" })).Should().HaveCount(2);
        (await _store.ListAsync("t1", new EdiFileQuery { Status = EdiFileStatus.Duplicate })).Should().ContainSingle();
        (await _store.ListAsync("t1", new EdiFileQuery { Direction = EdiFileDirection.Outbound })).Should().BeEmpty();
        (await _store.ListAsync("t1", new EdiFileQuery { From = T0.AddMinutes(30), To = T0.AddHours(2) })).Should().ContainSingle()
            .Which.Status.Should().Be(EdiFileStatus.Rejected);
        (await _store.ListAsync("t1", new EdiFileQuery { ControlNumber = "000000002" })).Should().HaveCount(2);
        (await _store.ListAsync("t1", new EdiFileQuery { ControlNumber = "11" })).Should().ContainSingle("GS06 matches");
        (await _store.ListAsync("t1", new EdiFileQuery { ControlNumber = "0002" })).Should().HaveCount(2, "ST02 matches");
        (await _store.ListAsync("t1", new EdiFileQuery { Limit = 1 })).Should().ContainSingle();
    }

    [Fact]
    public async Task GetAndSave_AreScopedToTheTenant()
    {
        var file = (await _store.ListAsync("t2", new EdiFileQuery())).Single();
        (await _store.GetAsync("t1", file.Id)).Should().BeNull();

        var stolen = (await _store.GetAsync("t2", file.Id))!;
        stolen.TenantId = "t1";
        stolen.Status = "Tampered";
        await _store.SaveAsync(stolen);
        (await _store.GetAsync("t2", file.Id))!.Status.Should().Be(EdiFileStatus.Applied);
    }

    [Fact]
    public async Task CreatesItsIndexes()
    {
        var names = await (await _db.GetCollection<EdiFile>(MongoEdiFileStore.CollectionName).Indexes.ListAsync()).ToListAsync();
        names.Select(i => i["name"].AsString).Should().Contain(["TenantId_1_ReceivedAt_-1", "TenantId_1_InterchangeControlNumbers_1"]);
    }
}
