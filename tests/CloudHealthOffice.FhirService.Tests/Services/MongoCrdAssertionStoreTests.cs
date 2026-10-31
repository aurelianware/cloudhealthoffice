using CloudHealthOffice.Testing.Mongo;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>The durable store of CRD coverage assertions on a real mongod (G-44).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoCrdAssertionStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoCrdAssertionStore _store = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public MongoCrdAssertionStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("crd_assertions");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName })
            .Build();
        _store = new MongoCrdAssertionStore(_mongo.Client, configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static CrdCoverageAssertion Assertion(string id, string tenant = "t", string? order = "ServiceRequest/sr-1") => new()
    {
        TenantId = tenant,
        AssertionId = id,
        CoverageReference = "Coverage/cov-1",
        OrderReference = order,
        BillingCodeSystem = "http://www.ama-assn.org/go/cpt",
        BillingCode = "72148",
        Determination = CrdDetermination.DocumentationRequired,
        Questionnaires = ["https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0"],
        AssertedAt = Now,
        ExpiresAt = Now.AddDays(90),
    };

    [Fact]
    public async Task An_assertion_round_trips_in_its_tenant_only()
    {
        await _store.SaveAsync([Assertion("a-1"), Assertion("a-2", order: null)]);

        (await _store.FindAsync("t", "a-1", Now)).Should().BeEquivalentTo(Assertion("a-1"));
        (await _store.FindAsync("t", "a-2", Now))!.OrderReference.Should().BeNull();
        (await _store.FindAsync("t2", "a-1", Now)).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_assertion_is_not_answered()
        => (await SaveThenFind(Now.AddDays(91))).Should().BeNull();

    [Fact]
    public async Task The_same_id_cannot_be_issued_twice_in_a_tenant()
    {
        await _store.SaveAsync([Assertion("a-1")]);

        var again = () => _store.SaveAsync([Assertion("a-1")]);

        await again.Should().ThrowAsync<MongoException>();
        await _store.SaveAsync([Assertion("a-1", tenant: "t2")]);
    }

    [Fact]
    public async Task Expired_assertions_are_removed_by_a_ttl_index()
    {
        var indexes = await (await _database.GetCollection<BsonDocument>(MongoCrdAssertionStore.CollectionName)
            .Indexes.ListAsync()).ToListAsync();

        indexes.Should().Contain(i => i["name"] == "ttl_expiresAt" && i["expireAfterSeconds"].ToInt64() == 0);
    }

    private async Task<CrdCoverageAssertion?> SaveThenFind(DateTimeOffset at)
    {
        await _store.SaveAsync([Assertion("a-1")]);
        return await _store.FindAsync("t", "a-1", at);
    }
}
