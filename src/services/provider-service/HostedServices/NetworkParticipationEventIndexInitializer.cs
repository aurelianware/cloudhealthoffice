using ProviderService.Models;
using MongoDB.Driver;

namespace ProviderService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>ProviderParticipationEvents</c>
/// stream (capability 5.5). Mirrors
/// <see cref="ProviderVerificationEventIndexInitializer"/>: indexes are
/// what make <c>MongoNetworkParticipationEventPublisher</c>'s
/// monotonic-version retry loop correct.
///
/// Idempotent — Mongo silently no-ops indexes that already exist with
/// the same spec.
/// </summary>
public sealed class NetworkParticipationEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<NetworkParticipationEventIndexInitializer> _logger;

    public NetworkParticipationEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<NetworkParticipationEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:ProviderParticipationEventsContainer"]
            ?? "ProviderParticipationEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<NetworkParticipationEvent>(_collectionName);

        var idemKeys = Builders<NetworkParticipationEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<NetworkParticipationEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_event" }),
            cancellationToken: cancellationToken);

        var orderKeys = Builders<NetworkParticipationEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<NetworkParticipationEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "NetworkParticipationEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
