using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using SmartAuthService.Models;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// EHR launch contexts live in MongoDB: single use (atomic), short-lived,
/// tenant- and client-scoped, and shared by every smart-auth-service instance.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class LaunchContextStoreTests
{
    private readonly SmartAuthTestFixture _fixture;

    public LaunchContextStoreTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Factory.Services.GetRequiredService<IMongoDatabase>();

    private MongoLaunchContextStore NewStore(TimeProvider? time = null)
        => new(Database, new ConfigurationBuilder().Build(), time);

    private static RegisterLaunchRequest Launch(string patient = "pat-001", string client = "ehr-app")
        => new() { PatientId = patient, EncounterId = "enc-1", ClientId = client };

    [Fact]
    public async Task RegisterAndConsume_ReturnsTheRegisteredContext()
    {
        var store = NewStore();
        var token = await store.RegisterAsync("tenant-a", "actor-1", Launch("pat-002"));

        var context = await store.ConsumeAsync(token, "tenant-a", "ehr-app");

        context.Should().NotBeNull();
        context!.TenantId.Should().Be("tenant-a");
        context.PatientId.Should().Be("pat-002");
        context.EncounterId.Should().Be("enc-1");
        context.RegisteredBy.Should().Be("actor-1");
    }

    [Fact]
    public async Task Consume_IsSingleUse()
    {
        var store = NewStore();
        var token = await store.RegisterAsync("tenant-a", "actor", Launch());

        (await store.ConsumeAsync(token, "tenant-a", "ehr-app")).Should().NotBeNull();
        (await store.ConsumeAsync(token, "tenant-a", "ehr-app")).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentUses_ExactlyOneSucceeds_EvenAcrossInstances()
    {
        var token = await NewStore().RegisterAsync("tenant-a", "actor", Launch());
        var stores = Enumerable.Range(0, 4).Select(_ => NewStore()).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => stores[i % stores.Count].ConsumeAsync(token, "tenant-a", "ehr-app")));

        results.Count(r => r != null).Should().Be(1);
    }

    [Fact]
    public async Task ALaunchRegisteredThroughOneInstance_IsConsumableThroughAnother()
    {
        var token = await NewStore().RegisterAsync("tenant-a", "actor", Launch());

        (await NewStore().ConsumeAsync(token, "tenant-a", "ehr-app")).Should().NotBeNull();
    }

    [Fact]
    public async Task ExpiredLaunch_IsRefused()
    {
        var clock = new MutableTime(DateTimeOffset.UtcNow);
        var store = NewStore(clock);
        var token = await store.RegisterAsync("tenant-a", "actor", Launch());

        clock.Now = clock.Now.AddMinutes(5).AddSeconds(1); // default TTL is 5 minutes

        (await store.ConsumeAsync(token, "tenant-a", "ehr-app")).Should().BeNull();
    }

    [Fact]
    public async Task LaunchFromAnotherTenantOrClient_IsRefused_AndNotBurned()
    {
        var store = NewStore();
        var token = await store.RegisterAsync("tenant-a", "actor", Launch());

        (await store.ConsumeAsync(token, "tenant-b", "ehr-app")).Should().BeNull();
        (await store.ConsumeAsync(token, "tenant-a", "other-app")).Should().BeNull();

        (await store.ConsumeAsync(token, "tenant-a", "ehr-app")).Should().NotBeNull(
            "a wrong-tenant attempt must not consume the launch for its rightful tenant");
    }

    [Fact]
    public async Task UnknownToken_ReturnsNull()
        => (await NewStore().ConsumeAsync("nonexistent-token-xyz", "tenant-a", "ehr-app")).Should().BeNull();

    [Fact]
    public async Task TheLaunchTokenIsNotStored_AndATtlIndexExpiresDocuments()
    {
        var token = await NewStore().RegisterAsync("tenant-a", "actor", Launch());
        var collection = Database.GetCollection<BsonDocument>(MongoLaunchContextStore.CollectionName);

        var stored = (await collection.Find(new BsonDocument()).ToListAsync()).Select(d => d.ToJson()).ToList();
        stored.Should().NotBeEmpty();
        stored.Should().NotContain(json => json.Contains(token));

        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        indexes.Should().Contain(i => i["name"] == "ttl_expires_at" && i.Contains("expireAfterSeconds"));
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
