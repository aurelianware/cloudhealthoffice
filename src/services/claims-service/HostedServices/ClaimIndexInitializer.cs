using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the <c>Claims</c> collection once at
/// startup. Moved out of <see cref="Repositories.ClaimRepositoryMongo"/> so
/// that scoped repository construction stays side-effect free regardless of
/// how often it is resolved per request.
///
/// Includes both the general-purpose claim lookup indexes and the compound
/// accumulator-rebuild indexes that support Redis cache-miss aggregation by
/// tenant / plan / owner / service date.
///
/// Idempotent: Mongo silently no-ops an index that already exists with the
/// same spec.
/// </summary>
public sealed class ClaimIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<ClaimIndexInitializer> _logger;

    public ClaimIndexInitializer(
        IMongoDatabase db,
        ILogger<ClaimIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _db.GetCollection<Claim>("Claims");
        var keys = Builders<Claim>.IndexKeys;

        var indexes = new[]
        {
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.ClaimNumber)),
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.MemberId)),
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.SubmittedDate)),
            // Compound index for search
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.ServiceDateFrom)),
            // Versioning chain key index — supports GetLatestVersion / ListVersions.
            new CreateIndexModel<Claim>(keys.Ascending(c => c.TenantId).Ascending(c => c.ClaimVersionId).Descending(c => c.VersionNumber)),
            // Accumulator rebuild indexes — support Redis cache-miss aggregation by owner/plan/year.
            new CreateIndexModel<Claim>(keys
                .Ascending(c => c.TenantId)
                .Ascending(c => c.BenefitPlanId)
                .Ascending(c => c.MemberId)
                .Ascending(c => c.ServiceDateFrom)),
            new CreateIndexModel<Claim>(keys
                .Ascending(c => c.TenantId)
                .Ascending(c => c.BenefitPlanId)
                .Ascending(c => c.SubscriberId)
                .Ascending(c => c.ServiceDateFrom)),
        };

        collection.Indexes.CreateMany(indexes, cancellationToken);

        var txnCollection = _db.GetCollection<ClaimImportTransaction>(
            Repositories.ClaimImportTransactionRepositoryMongo.CollectionName);
        txnCollection.Indexes.CreateOne(new CreateIndexModel<ClaimImportTransaction>(
            Builders<ClaimImportTransaction>.IndexKeys
                .Ascending(t => t.TenantId)
                .Descending(t => t.ReceivedAt)),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Claim indexes ensured on collection 'Claims'.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
