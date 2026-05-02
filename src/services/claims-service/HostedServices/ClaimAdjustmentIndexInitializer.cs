using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>ClaimAdjustments</c>
/// collection (capability 5.12a) once at startup. Mirrors
/// <see cref="ClaimVersionEventIndexInitializer"/> — a hosted service so
/// repository construction stays side-effect free regardless of
/// scoped-resolution frequency.
///
/// The indexes are what enforce 5.12a's correctness invariants:
/// the depth=1 invariant (one in-flight ClaimAdjustment per chain)
/// AND the Idempotency-Key uniqueness per tenant. Without these
/// unique indexes the early-placeholder-insert pattern in
/// <c>ClaimAdjustmentService.CreateAdjustmentAsync</c> cannot serialize
/// concurrent requests on the same chain or with the same idempotency
/// key.
///
/// Idempotent: Mongo silently no-ops an index that already exists with
/// the same spec.
/// </summary>
public sealed class ClaimAdjustmentIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly string _collectionName;
    private readonly ILogger<ClaimAdjustmentIndexInitializer> _logger;

    public ClaimAdjustmentIndexInitializer(
        IMongoDatabase db,
        IConfiguration configuration,
        ILogger<ClaimAdjustmentIndexInitializer> logger)
    {
        _db = db;
        _collectionName = configuration["CosmosDb:ClaimAdjustmentsContainer"] ?? "ClaimAdjustments";
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<ClaimAdjustment>(_collectionName);
        var keys = Builders<ClaimAdjustment>.IndexKeys;

        var indexes = new[]
        {
            // Depth=1 invariant per Decision 11: at most one adjustment per chain per tenant.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.ClaimVersionId),
                new CreateIndexOptions { Unique = true, Name = "tenant_chain_unique" }),

            // Idempotency-Key uniqueness per Decision 6.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.IdempotencyKey),
                new CreateIndexOptions { Unique = true, Name = "tenant_idempotency_unique" }),

            // Status + createdAt for the 5.12b ReversalRun batch query.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.Status).Descending(x => x.CreatedAt)),

            // Predecessor lookup for the chain-scoped GET endpoint.
            new CreateIndexModel<ClaimAdjustment>(
                keys.Ascending(x => x.TenantId).Ascending(x => x.PredecessorClaimId)),
        };

        collection.Indexes.CreateMany(indexes, cancellationToken);

        _logger.LogInformation(
            "ClaimAdjustment indexes ensured on collection '{Collection}'.",
            _collectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
