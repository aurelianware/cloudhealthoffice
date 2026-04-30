using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.HostedServices;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Mongo-backed publisher coverage for capability 5.6: monotonic
/// <see cref="CredentialingEvent.Version"/> per
/// <c>(TenantId, ProviderId)</c>, idempotent re-publish on duplicate
/// <see cref="CredentialingEvent.EventId"/>, and cross-tenant
/// <c>_id</c> collision protection (PR 5.4.5 lesson).
/// </summary>
public class CredentialingEventPublisherTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";
    private const string ProviderId = "provider-001";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private MongoCredentialingEventPublisher _publisher = null!;

    public async Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"credentialing_event_test_{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().Build();
        _publisher = new MongoCredentialingEventPublisher(
            _database, config, NullLogger<MongoCredentialingEventPublisher>.Instance);
        // Ensure the unique (TenantId, ProviderId, Version) index so the
        // publisher's retry loop is exercised correctly under concurrency.
        var indexer = new CredentialingEventIndexInitializer(
            _database, config, NullLogger<CredentialingEventIndexInitializer>.Instance);
        await indexer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* EphemeralMongo / driver mismatch on disposal */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Publish_assigns_monotonic_version_per_provider()
    {
        var v1 = await _publisher.PublishAsync(BuildSubmitted(Tenant, ProviderId, "evt-1"));
        var v2 = await _publisher.PublishAsync(BuildSubmitted(Tenant, ProviderId, "evt-2"));
        var v3 = await _publisher.PublishAsync(BuildSubmitted(Tenant, ProviderId, "evt-3"));

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);
        v3.Version.Should().Be(3);
    }

    [Fact]
    public async Task Publish_with_duplicate_eventId_is_idempotent()
    {
        var evt = BuildSubmitted(Tenant, ProviderId, "evt-dup");
        var first = await _publisher.PublishAsync(evt);
        var second = await _publisher.PublishAsync(BuildSubmitted(Tenant, ProviderId, "evt-dup"));

        first.EventId.Should().Be(second.EventId);
        first.Version.Should().Be(second.Version);
    }

    [Fact]
    public async Task Publish_assigns_partition_scoped_id()
    {
        var evt = await _publisher.PublishAsync(BuildSubmitted(Tenant, ProviderId, "evt-id"));
        evt.PartitionKey.Should().Be($"{Tenant}:{ProviderId}");
        evt.Id.Should().Be($"{Tenant}:{ProviderId}:evt-id");
    }

    [Fact]
    public async Task Publish_cross_tenant_same_eventId_does_not_collide()
    {
        // Two tenants append events with the same EventId (e.g. two
        // tenants running the legacy PUT /credentialing at the same
        // instant for providers that happen to share a logical id).
        // Without the _id = PartitionKey:EventId scope, Mongo would
        // throw on the second insert.
        var a = await _publisher.PublishAsync(BuildSubmitted("tenant-a", "p-1", "common"));
        var b = await _publisher.PublishAsync(BuildSubmitted("tenant-b", "p-1", "common"));

        a.PartitionKey.Should().Be("tenant-a:p-1");
        b.PartitionKey.Should().Be("tenant-b:p-1");
        a.Id.Should().NotBe(b.Id);
    }

    [Fact]
    public async Task Publish_concurrent_inserts_serialize_via_retry_loop()
    {
        var tasks = Enumerable.Range(1, 5)
            .Select(i => _publisher.PublishAsync(BuildSubmitted(Tenant, ProviderId, $"evt-c{i}")))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        var versions = results.Select(r => r.Version).OrderBy(v => v).ToList();
        versions.Should().Equal(new[] { 1, 2, 3, 4, 5 });
    }

    private static CredentialingEvent BuildSubmitted(string tenantId, string providerId, string eventId) => new()
    {
        TenantId = tenantId,
        ProviderId = providerId,
        EventId = eventId,
        EventType = CredentialingEventType.ApplicationSubmitted,
    };
}
