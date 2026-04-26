using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>PlanVersionEvents</c> stream
/// once at startup. Mirrors <c>MemberEventIndexInitializer</c> in
/// <c>member-service</c>: a hosted service so repository construction
/// stays side-effect free.
///
/// The indexes are what make
/// <see cref="Services.MongoPlanVersionEventPublisher"/>'s retry loop
/// correct — without the unique index on
/// <c>(TenantId, PlanId, Version)</c>, concurrent writers can each insert
/// with the same <c>Version</c> and the duplicate-key catch never fires.
///
/// Idempotent: Mongo silently no-ops an index that already exists with
/// the same spec.
/// </summary>
public sealed class PlanVersionEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<PlanVersionEventIndexInitializer> _logger;

    public PlanVersionEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<PlanVersionEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:PlanVersionEventsContainer"] ?? "PlanVersionEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<PlanVersionEvent>(_collectionName);

        // (TenantId, PlanId, EventId) — idempotency key.
        var idemKeys = Builders<PlanVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.PlanId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<PlanVersionEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_plan_event" }),
            cancellationToken: cancellationToken);

        // (TenantId, PlanId, Version) — monotonic-ordering invariant.
        // The publisher's retry-on-DuplicateKey loop relies on this.
        var orderKeys = Builders<PlanVersionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.PlanId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<PlanVersionEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_plan_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "PlanVersionEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
