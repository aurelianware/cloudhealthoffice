using ProviderService.Models;
using MongoDB.Driver;

namespace ProviderService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>ProviderVerificationEvents</c>
/// stream (capability 5.4.5). Mirrors
/// <see cref="ProviderVersionEventIndexInitializer"/>: indexes are what
/// make <c>MongoProviderVerificationEventPublisher</c>'s monotonic-version
/// retry loop correct.
///
/// Idempotent — Mongo silently no-ops indexes that already exist with
/// the same spec.
/// </summary>
public sealed class ProviderVerificationEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<ProviderVerificationEventIndexInitializer> _logger;

    public ProviderVerificationEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<ProviderVerificationEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:ProviderVerificationEventsContainer"]
            ?? "ProviderVerificationEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<ProviderVerificationEvent>(_collectionName);

        var idemKeys = Builders<ProviderVerificationEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ProviderVerificationEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_event" }),
            cancellationToken: cancellationToken);

        var orderKeys = Builders<ProviderVerificationEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ProviderVerificationEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "ProviderVerificationEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
