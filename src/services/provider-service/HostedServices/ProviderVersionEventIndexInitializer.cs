using ProviderService.Models;
using MongoDB.Driver;

namespace ProviderService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>ProviderVersionEvents</c> stream
/// once at startup. Mirrors <c>PlanVersionEventIndexInitializer</c> in
/// <c>benefit-plan-service</c>: a hosted service so repository construction
/// stays side-effect free.
///
/// The indexes are what make
/// <see cref="Services.MongoProviderVersionEventPublisher"/>'s retry loop
/// correct — without the unique index on
/// <c>(TenantId, ProviderId, Version)</c>, concurrent writers can each insert
/// with the same <c>Version</c> and the duplicate-key catch never fires.
///
/// Idempotent: Mongo silently no-ops an index that already exists with
/// the same spec.
/// </summary>
public sealed class ProviderVersionEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<ProviderVersionEventIndexInitializer> _logger;

    public ProviderVersionEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<ProviderVersionEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:ProviderVersionEventsContainer"] ?? "ProviderVersionEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<ProviderVersionEvent>(_collectionName);

        // (TenantId, ProviderId, EventId) — idempotency key.
        var idemKeys = Builders<ProviderVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ProviderVersionEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_event" }),
            cancellationToken: cancellationToken);

        // (TenantId, ProviderId, Version) — monotonic-ordering invariant.
        // The publisher's retry-on-DuplicateKey loop relies on this.
        var orderKeys = Builders<ProviderVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ProviderId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ProviderVersionEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_provider_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "ProviderVersionEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
