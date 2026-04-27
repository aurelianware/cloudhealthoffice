using EphemeralMongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Services;

/// <summary>
/// Mongo-backed publisher: idempotency on duplicate <see cref="ProviderVersionEvent.EventId"/>
/// and monotonic <see cref="ProviderVersionEvent.Version"/> per
/// <c>(TenantId, ProviderId)</c>.
/// </summary>
public class ProviderVersionEventPublisherTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";
    private const string ProviderId = "provider-001";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private MongoProviderVersionEventPublisher _publisher = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase("provider_event_test");
        var config = new ConfigurationBuilder().Build();
        _publisher = new MongoProviderVersionEventPublisher(
            _database, config, NullLogger<MongoProviderVersionEventPublisher>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* EphemeralMongo / MongoDB.Driver 3.x mismatch on disposal */ }
        return Task.CompletedTask;
    }

    private static Provider SampleVersion(string versionId, int n = 1) => new()
    {
        Id = "doc-" + versionId,
        ProviderId = ProviderId,
        TenantId = Tenant,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        VersionId = versionId,
        VersionNumber = n,
        VersionState = ProviderVersionState.Active,
        ActivatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Publish_assigns_monotonic_version_per_provider()
    {
        var v1 = await _publisher.PublishVersionActivatedAsync(SampleVersion("V1"), "user", null);
        var v2 = await _publisher.PublishVersionSuspendedAsync(SampleVersion("V1"), "review", "user", null);
        var v3 = await _publisher.PublishVersionTerminatedAsync(SampleVersion("V1"), "left network", "user", null);

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);
        v3.Version.Should().Be(3);
    }

    [Fact]
    public async Task Publish_with_duplicate_eventId_is_idempotent()
    {
        var version = SampleVersion("V2");
        var first = await _publisher.PublishVersionActivatedAsync(version, "user", null);
        var second = await _publisher.PublishVersionActivatedAsync(version, "user", null);

        first.EventId.Should().Be(second.EventId);
        first.Version.Should().Be(second.Version);
    }
}
