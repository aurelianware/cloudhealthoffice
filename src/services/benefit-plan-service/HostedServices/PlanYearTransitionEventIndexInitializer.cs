using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.HostedServices;

/// <summary>
/// Creates Mongo indexes used by the
/// <c>PlanYearTransitionEvents</c> stream once at startup. Mirrors
/// <see cref="PlanVersionEventIndexInitializer"/> in this service and
/// <c>MemberEventIndexInitializer</c> in member-service.
///
/// The indexes are what make
/// <see cref="Services.MongoPlanYearTransitionPublisher"/>'s retry loop
/// correct — without the unique index on
/// <c>(TenantId, PlanId, Version)</c>, two scheduler replicas racing
/// could each insert with the same <c>Version</c> and the
/// duplicate-key catch never fires.
///
/// Idempotent: Mongo silently no-ops an index that already exists with
/// the same spec.
/// </summary>
public sealed class PlanYearTransitionEventIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<PlanYearTransitionEventIndexInitializer> _logger;

    public PlanYearTransitionEventIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<PlanYearTransitionEventIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:PlanYearTransitionEventsContainer"]
            ?? "PlanYearTransitionEvents";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<PlanYearTransitionEvent>(_collectionName);

        // (TenantId, PlanId, EventId) — idempotency key. The publisher's
        // pre-insert lookup uses this; the unique constraint is what
        // makes "scheduler runs twice" a no-op.
        var idemKeys = Builders<PlanYearTransitionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.PlanId)
            .Ascending(x => x.EventId);
        collection.Indexes.CreateOne(
            new CreateIndexModel<PlanYearTransitionEvent>(
                idemKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_plan_event" }),
            cancellationToken: cancellationToken);

        // (TenantId, PlanId, Version) — monotonic-ordering invariant.
        var orderKeys = Builders<PlanYearTransitionEvent>.IndexKeys
            .Ascending(x => x.TenantId)
            .Ascending(x => x.PlanId)
            .Ascending(x => x.Version);
        collection.Indexes.CreateOne(
            new CreateIndexModel<PlanYearTransitionEvent>(
                orderKeys,
                new CreateIndexOptions { Unique = true, Name = "ux_tenant_plan_version" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "PlanYearTransitionEvent indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
