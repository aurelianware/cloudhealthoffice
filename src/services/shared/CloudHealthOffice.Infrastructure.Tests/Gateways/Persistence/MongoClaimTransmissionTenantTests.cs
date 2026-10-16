using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Persistence;
using EphemeralMongo;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Persistence;

/// <summary>
/// The tenant-scoped transmission lookup over real MongoDB (EphemeralMongo):
/// a caller's tenant never reads another tenant's transmission by id. The
/// unscoped lookup remains for gateway correlation (a 277CA names only the
/// transmission), where the record's own tenant is authoritative.
/// </summary>
public sealed class MongoClaimTransmissionTenantTests(MongoClaimTransmissionTenantTests.Mongod mongo)
    : IClassFixture<MongoClaimTransmissionTenantTests.Mongod>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private IClaimTransmissionStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"claim_transmission_tenant_{Guid.NewGuid():N}");
        var store = new MongoClaimLifecycleStore(_db, new ClaimLifecycleOptions());
        await store.EnsureIndexesAsync(CancellationToken.None);
        _store = store;
        await _store.SaveAsync(new ClaimTransmissionRecord
        {
            TransmissionId = "tx-alpha-1",
            TenantId = "tenant-alpha",
            ClaimId = "CLM-1",
            GatewayName = "Mock",
            IdempotencyKey = "k-1",
        });
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    /// <summary>
    /// The index set is accepted by a real MongoDB, including the unique
    /// tenant + idempotency-key index that de-duplicates submissions. It used
    /// to fail on a <c>unique</c> <c>_id</c> spec, so none was created.
    /// </summary>
    [Fact]
    public async Task EnsureIndexes_CreatesTheUniqueIdempotencyIndex()
    {
        var indexes = await (await _db.GetCollection<MongoDB.Bson.BsonDocument>(new ClaimLifecycleOptions().TransmissionsCollection)
            .Indexes.ListAsync()).ToListAsync();

        indexes.Should().Contain(i => i.Contains("unique") && i["unique"].AsBoolean
            && i["key"].AsBsonDocument.Contains("TenantId") && i["key"].AsBsonDocument.Contains("IdempotencyKey"));
    }

    [Fact]
    public async Task OwnTenant_ReadsTheTransmission()
        => (await _store.GetByTenantAndIdAsync("tenant-alpha", "tx-alpha-1"))!.ClaimId.Should().Be("CLM-1");

    [Fact]
    public async Task AnotherTenant_ReadsNothing()
        => (await _store.GetByTenantAndIdAsync("tenant-beta", "tx-alpha-1")).Should().BeNull();

    [Fact]
    public async Task GatewayCorrelation_StillFindsItById()
        => (await _store.GetByIdAsync("tx-alpha-1"))!.TenantId.Should().Be("tenant-alpha");

    /// <summary>
    /// A mongod for this class only. This project is referenced by other test
    /// projects, so it does not link the shared public MongoRunnerFixture
    /// (tests/Shared/EphemeralMongo), whose type would clash with theirs.
    /// </summary>
    public sealed class Mongod : IDisposable
    {
        private readonly IMongoRunner _runner = MongoRunner.Run(new MongoRunnerOptions
        {
            Version = MongoVersion.V7,
            Edition = MongoEdition.Community,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
        });

        public Mongod() => Client = new MongoClient(_runner.ConnectionString);

        public MongoClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _runner.Dispose();
        }
    }
}
