using CloudHealthOffice.Testing.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests;

/// <summary>The subscription store against a real mongod, beside tenant-service's own Tenant documents.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class SubscriptionStoreTests : IAsyncLifetime
{
    private const string Directory = "aaaaaaaa-0000-0000-0000-000000000001";
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoSubscriptionStore _store = null!;

    public SubscriptionStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("subscriptions");
        _store = new MongoSubscriptionStore(_database);
        // A tenant-service Tenant document (no subscriptionStatus): never touched by the store.
        await _database.GetCollection<BsonDocument>("Tenants").InsertOneAsync(new BsonDocument
        {
            ["tenantId"] = "tenant-svc", ["tenantName"] = "Svc", ["status"] = "active", ["azureTenantId"] = Directory,
        });
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static SignupWrite Signup() => new()
    {
        AzureTenantId = Directory,
        AdminEmail = "owner@acme.example",
        OrganizationName = "Acme Health",
        Tier = "starter",
    };

    [Fact]
    public async Task Signup_CreatesATrial_ThenRefusesASecondForTheSameDirectory()
    {
        var created = await _store.SignupAsync(Signup(), CancellationToken.None);

        created!.SubscriptionStatus.Should().Be("Trial");
        created.IsDemo.Should().BeFalse();
        created.TrialEndsAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(14), TimeSpan.FromMinutes(1));
        created.AdminEmails.Should().Equal("owner@acme.example");
        created.TenantId.Should().StartWith("tenant-");

        (await _store.SignupAsync(Signup(), CancellationToken.None)).Should().BeNull();
        (await _store.ListAsync(CancellationToken.None)).Should().ContainSingle(s => s.AzureTenantId == Directory);
    }

    [Fact]
    public async Task PlatformWrites_TouchOnlySubscriptionRecords()
    {
        await _store.SignupAsync(Signup(), CancellationToken.None);

        (await _store.SetStatusAsync(Directory, "Active", CancellationToken.None)).Should().BeTrue();
        (await _store.UpdateAsync(Directory, new SubscriptionWrite { Tier = "enterprise", Notes = "upgraded" }, CancellationToken.None)).Should().BeTrue();
        var updated = (await _store.ListAsync(CancellationToken.None)).Single();
        updated.SubscriptionStatus.Should().Be("Active");
        updated.Tier.Should().Be("enterprise");

        (await _store.DeleteAsync(Directory, CancellationToken.None)).Should().BeTrue();
        (await _store.ListAsync(CancellationToken.None)).Should().BeEmpty();

        var tenantDoc = await _database.GetCollection<BsonDocument>("Tenants")
            .Find(Builders<BsonDocument>.Filter.Eq("tenantId", "tenant-svc")).SingleAsync();
        tenantDoc.Contains("subscriptionStatus").Should().BeFalse();
        (await _store.SetStatusAsync("unknown-directory", "Active", CancellationToken.None)).Should().BeFalse();
    }
}
