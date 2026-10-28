using CloudHealthOffice.Testing.Mongo;
using FhirService.Services.PayerToPayer.BulkMemberMatch;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.PayerToPayer;

/// <summary>The durable store of PDex <c>$bulk-member-match</c> Groups on a real mongod (G-35).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoPayerToPayerMatchGroupStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoPayerToPayerMatchGroupStore _store = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public MongoPayerToPayerMatchGroupStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("p2p_match_groups");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName })
            .Build();
        _store = new MongoPayerToPayerMatchGroupStore(_mongo.Client, configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static PayerToPayerMatchGroup Group(string id, string code = "match", string request = "req-1", string tenant = "t") => new()
    {
        TenantId = tenant,
        GroupId = id,
        RequestId = request,
        Code = code,
        ReceivingPayerId = "NEW-PLAN",
        RequestedBy = "new-plan-backend",
        MemberIds = code == "match" ? ["m-1", "m-2"] : [],
        Resource = """{"resourceType":"Group","id":"__"}""".Replace("__", id),
        CreatedAt = Now,
        ExpiresAt = Now.AddDays(30),
    };

    [Fact]
    public async Task AGroup_RoundTrips_InItsTenantOnly()
    {
        await _store.SaveAsync([Group("g-1")]);

        (await _store.GetAsync("t", "g-1", Now)).Should().BeEquivalentTo(Group("g-1"));
        (await _store.GetAsync("t2", "g-1", Now)).Should().BeNull();
    }

    [Fact]
    public async Task ARequestsGroups_AreListedTogether_UntilTheyExpire()
    {
        await _store.SaveAsync([Group("g-1"), Group("g-2", code: "nomatch"), Group("g-3", request: "req-2")]);

        (await _store.ListForRequestAsync("t", "req-1", Now)).Select(g => g.GroupId).Should().BeEquivalentTo(["g-1", "g-2"]);
        (await _store.ListForRequestAsync("t", "req-1", Now.AddDays(31))).Should().BeEmpty();
        (await _store.GetAsync("t", "g-1", Now.AddDays(31))).Should().BeNull("an expired Group is never answered");
    }
}
