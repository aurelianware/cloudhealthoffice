using ProviderService.Models;
using MongoDB.Driver;

namespace ProviderService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>CredentialingEvents</c>
/// stream (capability 5.6). Mirrors
/// <see cref="ProviderVerificationEventIndexInitializer"/>: indexes are
/// what make <c>MongoCredentialingEventPublisher</c>'s monotonic-version
/// retry loop correct.
///
/// Idempotent — Mongo silently no-ops indexes that already exist with
/// the same spec.
/// </summary>
public sealed class CredentialingEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<CredentialingEventIndexInitializer> _logger;

    public CredentialingEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<CredentialingEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:CredentialingEventsContainer"]
            ?? "CredentialingEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<CredentialingEvent>(_collectionName);

        var idemKeys = Builders<CredentialingEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<CredentialingEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_event" }),
            cancellationToken: cancellationToken);

        var orderKeys = Builders<CredentialingEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<CredentialingEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "CredentialingEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
