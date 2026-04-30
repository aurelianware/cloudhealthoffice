using ClaimsService.HostedServices;
using ClaimsService.Models;
using EphemeralMongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.HostedServices;

/// <summary>
/// The hosted index initializer must create the two unique indexes that the
/// <see cref="Services.MongoClaimVersionEventPublisher"/> retry loop relies
/// on. Without the unique <c>(TenantId, ClaimVersionId, Version)</c> index,
/// concurrent writers can each insert with the same Version and the
/// duplicate-key catch never fires — the chain ends up with gaps or
/// duplicates. These tests pin the indexes by name so a careless rename
/// caught at test time, not in production.
/// </summary>
public class ClaimVersionEventIndexInitializerTests : IAsyncLifetime
{
    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"claim_index_test_{Guid.NewGuid():N}");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* see ProviderVersionEventPublisherTests note */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_creates_both_unique_indexes()
    {
        var config = new ConfigurationBuilder().Build();
        var initializer = new ClaimVersionEventIndexInitializer(
            _database, config, NullLogger<ClaimVersionEventIndexInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        var collection = _database.GetCollection<ClaimVersionEvent>("ClaimVersionEvents");
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        var byName = indexes.ToDictionary(x => x["name"].AsString);

        byName.Should().ContainKey("ux_tenant_claim_event");
        byName.Should().ContainKey("ux_tenant_claim_version");

        byName["ux_tenant_claim_event"]["unique"].AsBoolean.Should().BeTrue();
        byName["ux_tenant_claim_version"]["unique"].AsBoolean.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_is_idempotent_on_repeat_run()
    {
        // Mongo silently no-ops an index that already exists with the same
        // spec; running the initializer a second time must not throw.
        var config = new ConfigurationBuilder().Build();
        var initializer = new ClaimVersionEventIndexInitializer(
            _database, config, NullLogger<ClaimVersionEventIndexInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);
        await initializer.StartAsync(CancellationToken.None);

        var collection = _database.GetCollection<ClaimVersionEvent>("ClaimVersionEvents");
        var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
        // _id, ux_tenant_claim_event, ux_tenant_claim_version → 3 indexes
        indexes.Should().HaveCount(3);
    }

    [Fact]
    public async Task Configuration_override_routes_to_alternate_collection()
    {
        // Ops sometimes deploy with a non-default collection name (e.g. for
        // staging cohabitation). The initializer must honor the
        // CosmosDb:ClaimVersionEventsContainer override.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CosmosDb:ClaimVersionEventsContainer"] = "AltClaimVersionEvents"
            })
            .Build();
        var initializer = new ClaimVersionEventIndexInitializer(
            _database, config, NullLogger<ClaimVersionEventIndexInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        // The alternate collection got the two named indexes (plus the
        // implicit _id index Mongo creates on first write).
        var altCollection = _database.GetCollection<ClaimVersionEvent>("AltClaimVersionEvents");
        var altIndexes = await (await altCollection.Indexes.ListAsync()).ToListAsync();
        var altByName = altIndexes.ToDictionary(x => x["name"].AsString);
        altByName.Should().ContainKey("ux_tenant_claim_event");
        altByName.Should().ContainKey("ux_tenant_claim_version");

        // The default collection was never touched by this initializer; it
        // may not have been materialized at all (Mongo lazy-creates on
        // first write or first index op). What matters is that NEITHER
        // named index landed there.
        var defaultCollection = _database.GetCollection<ClaimVersionEvent>("ClaimVersionEvents");
        var defaultIndexes = await (await defaultCollection.Indexes.ListAsync()).ToListAsync();
        defaultIndexes.Should().NotContain(x => x["name"].AsString == "ux_tenant_claim_event");
        defaultIndexes.Should().NotContain(x => x["name"].AsString == "ux_tenant_claim_version");
    }
}
