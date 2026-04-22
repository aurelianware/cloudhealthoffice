using ConsentService.Models;
using ConsentService.Repositories;
using MongoDB.Driver;

namespace ConsentService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the Consent and ConsentEvent
/// collections once at startup. Running from a hosted service (rather than
/// the repository constructor) keeps construction side-effect free and
/// lets us register repositories as singletons.
///
/// Idempotent: Mongo silently no-ops an index that already exists with the
/// same spec.
/// </summary>
public sealed class ConsentIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<ConsentIndexInitializer> _logger;

    public ConsentIndexInitializer(IMongoDatabase db, ILogger<ConsentIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var consents = _db.GetCollection<Consent>(ConsentRepositoryMongo.ConsentsCollectionName);

        await consents.Indexes.CreateOneAsync(
            new CreateIndexModel<Consent>(
                Builders<Consent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.MemberId)
                    .Descending(x => x.CreatedAt),
                new CreateIndexOptions { Name = "ix_tenant_member_created" }),
            cancellationToken: cancellationToken);

        await consents.Indexes.CreateOneAsync(
            new CreateIndexModel<Consent>(
                Builders<Consent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Id),
                new CreateIndexOptions { Name = "ix_tenant_id", Unique = true }),
            cancellationToken: cancellationToken);

        var events = _db.GetCollection<ConsentEvent>(ConsentEventRepositoryMongo.ConsentEventsCollectionName);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<ConsentEvent>(
                Builders<ConsentEvent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.ConsentId)
                    .Ascending(x => x.EventId),
                new CreateIndexOptions { Name = "ux_tenant_consent_event", Unique = true }),
            cancellationToken: cancellationToken);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<ConsentEvent>(
                Builders<ConsentEvent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.ConsentId)
                    .Ascending(x => x.OccurredAt),
                new CreateIndexOptions { Name = "ix_tenant_consent_occurred" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Consent, ConsentEvent indexes ensured.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
