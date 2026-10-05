using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Security;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.PricingApi.Tests.Security;

/// <summary>
/// Keys stored in plaintext before hashing are hashed at startup
/// (<see cref="MongoApiKeyRepository.InitializeAsync"/>), against a real mongod.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoApiKeyRepositoryMigrationTests : IAsyncLifetime
{
    private const string KeyA = "cho_0123456789abcdef0123456789abcdef";
    private const string KeyB = "cho_fedcba9876543210fedcba9876543210";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public MongoApiKeyRepositoryMigrationTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("pricing_keys_migration");

        // The legacy shape: plaintext key, a unique index on it, usage rows carrying it.
        var keys = _database.GetCollection<BsonDocument>(MongoApiKeyRepository.KeysCollection);
        await keys.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("ApiKey"), new CreateIndexOptions { Unique = true }));
        await keys.InsertManyAsync(new[]
        {
            Legacy(KeyA, "Acme Health", active: true),
            Legacy(KeyB, "Beta Plan", active: false),
        });
        await _database.GetCollection<BsonDocument>(MongoApiKeyRepository.UsageCollection).InsertManyAsync(new[]
        {
            Usage(KeyA), Usage(KeyA), Usage(KeyB), Usage("cho_deleted0000000000000000000000000"),
        });
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    [Fact]
    public async Task InitializeAsync_HashesPlaintextKeys_AndRemovesThePlaintextEverywhere()
    {
        var repository = new MongoApiKeyRepository(_database, NullLogger<MongoApiKeyRepository>.Instance);

        await repository.InitializeAsync();

        var keys = await _database.GetCollection<BsonDocument>(MongoApiKeyRepository.KeysCollection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        keys.Should().HaveCount(2);
        keys.Should().OnlyContain(d => !d.Contains("ApiKey"));
        keys.ToJson().Should().NotContain(KeyA[4..]).And.NotContain(KeyB[4..]);

        var usage = await _database.GetCollection<BsonDocument>(MongoApiKeyRepository.UsageCollection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        usage.Should().OnlyContain(d => !d.Contains("ApiKey"));
        usage.ToJson().Should().NotContain(KeyA[4..]).And.NotContain("deleted0000");

        var a = await repository.GetByHashAsync(ApiKeyHashing.Hash(KeyA));
        a.Should().NotBeNull();
        a!.TenantName.Should().Be("Acme Health");
        a.KeyPrefix.Should().Be(KeyA[..12]);
        a.KeyId.Should().StartWith("pk_");
        a.IsActive.Should().BeTrue();
        (await repository.GetByIdAsync(a.KeyId))!.KeyHash.Should().Be(ApiKeyHashing.Hash(KeyA));

        usage.Count(d => d.GetValue("KeyId", BsonNull.Value) == a.KeyId).Should().Be(2);
        (await repository.GetByHashAsync(ApiKeyHashing.Hash(KeyB)))!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndNewKeysStoreNoPlaintext()
    {
        var repository = new MongoApiKeyRepository(_database, NullLogger<MongoApiKeyRepository>.Instance);
        await repository.InitializeAsync();
        var keyIdBefore = (await repository.GetByHashAsync(ApiKeyHashing.Hash(KeyA)))!.KeyId;

        await repository.InitializeAsync();

        (await repository.GetByHashAsync(ApiKeyHashing.Hash(KeyA)))!.KeyId.Should().Be(keyIdBefore);

        // Two new keys: the old unique index on the plaintext field would refuse the second.
        var (k1, r1) = ApiKeyHashing.Issue("C1", null, PricingTier.Free, 1_000, "admin");
        var (k2, r2) = ApiKeyHashing.Issue("C2", null, PricingTier.Free, 1_000, "admin");
        await repository.CreateAsync(r1);
        await repository.CreateAsync(r2);
        await new MongoUsageRepository(_database).RecordUsageAsync(new UsageRecord
        {
            KeyId = r1.KeyId, Endpoint = "reprice", LineCount = 1, Timestamp = DateTimeOffset.UtcNow
        });
        await new MongoUsageRepository(_database).RecordUsageAsync(new UsageRecord
        {
            KeyId = r1.KeyId, Endpoint = "reprice", LineCount = 1, Timestamp = DateTimeOffset.UtcNow
        });

        var raw = (await _database.GetCollection<BsonDocument>(MongoApiKeyRepository.KeysCollection)
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ToJson();
        raw.Should().NotContain(k1).And.NotContain(k2);
        (await repository.ListAsync()).Should().HaveCount(4);
    }

    private static BsonDocument Legacy(string key, string tenant, bool active) => new()
    {
        { "_id", ObjectId.GenerateNewId() },
        { "ApiKey", key },
        { "TenantName", tenant },
        { "Tier", 0 },
        { "MonthlyLimit", 1000 },
        { "CurrentMonthUsage", 5 },
        { "CreatedAt", new BsonArray { DateTime.UtcNow.Ticks, 0 } },
        { "IsActive", active },
    };

    private static BsonDocument Usage(string key) => new()
    {
        { "_id", ObjectId.GenerateNewId() },
        { "ApiKey", key },
        { "Endpoint", "reprice" },
        { "LineCount", 1 },
        { "Timestamp", new BsonArray { DateTime.UtcNow.Ticks, 0 } },
        { "ResponseTimeMs", 0 },
        { "Success", true },
    };
}
