using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Persistence;
using CloudHealthOffice.Testing.Mongo;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Persistence;

/// <summary>
/// The tenant-scoped transmission lookup over real MongoDB (EphemeralMongo):
/// a caller's tenant never reads another tenant's transmission by id. The
/// unscoped lookup remains for gateway correlation (a 277CA names only the
/// transmission), where the record's own tenant is authoritative.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoClaimTransmissionTenantTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private IClaimTransmissionStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("claim_transmission_tenant");
        // No EnsureIndexesAsync: on MongoDB 7 it fails on its unique _id index
        // spec (pre-existing; reported separately). The lookups need no index.
        _store = new MongoClaimLifecycleStore(_db, new ClaimLifecycleOptions());
        await _store.SaveAsync(new ClaimTransmissionRecord
        {
            TransmissionId = "tx-alpha-1",
            TenantId = "tenant-alpha",
            ClaimId = "CLM-1",
            GatewayName = "Mock",
            IdempotencyKey = "k-1",
        });
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    [Fact]
    public async Task OwnTenant_ReadsTheTransmission()
        => (await _store.GetByTenantAndIdAsync("tenant-alpha", "tx-alpha-1"))!.ClaimId.Should().Be("CLM-1");

    [Fact]
    public async Task AnotherTenant_ReadsNothing()
        => (await _store.GetByTenantAndIdAsync("tenant-beta", "tx-alpha-1")).Should().BeNull();

    [Fact]
    public async Task GatewayCorrelation_StillFindsItById()
        => (await _store.GetByIdAsync("tx-alpha-1"))!.TenantId.Should().Be("tenant-alpha");
}
