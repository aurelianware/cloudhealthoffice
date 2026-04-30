using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>ClaimVersionEvents</c> stream
/// once at startup. Mirrors <c>ProviderVersionEventIndexInitializer</c> and
/// <c>PlanVersionEventIndexInitializer</c>: a hosted service so repository
/// construction stays side-effect free.
///
/// The indexes are what make
/// <see cref="Services.MongoClaimVersionEventPublisher"/>'s retry loop
/// correct — without the unique index on
/// <c>(TenantId, ClaimVersionId, Version)</c>, concurrent writers can each
/// insert with the same <c>Version</c> and the duplicate-key catch never
/// fires.
///
/// Idempotent: Mongo silently no-ops an index that already exists with
/// the same spec.
/// </summary>
public sealed class ClaimVersionEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<ClaimVersionEventIndexInitializer> _logger;

    public ClaimVersionEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<ClaimVersionEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:ClaimVersionEventsContainer"] ?? "ClaimVersionEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<ClaimVersionEvent>(_collectionName);

        // (TenantId, ClaimVersionId, EventId) — idempotency key.
        var idemKeys = Builders<ClaimVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ClaimVersionId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ClaimVersionEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_claim_event" }),
            cancellationToken: cancellationToken);

        // (TenantId, ClaimVersionId, Version) — monotonic-ordering invariant.
        // The publisher's retry-on-DuplicateKey loop relies on this.
        var orderKeys = Builders<ClaimVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.ClaimVersionId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<ClaimVersionEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_claim_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "ClaimVersionEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
