using AppealsService.Models;
using AppealsService.Repositories;
using MongoDB.Driver;

namespace AppealsService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the Appeals and AppealEvents
/// collections once at startup. Idempotent — Mongo silently no-ops an
/// index that already exists with the same spec.
///
/// Registered AFTER <see cref="AppealStatusMigrationHostedService"/> so
/// any pre-modernization records are rewritten to the current schema
/// before the unique <c>ux_tenant_appeal_number</c> index is built —
/// otherwise a duplicate <c>AppealNumber</c> under the same tenant would
/// fail index creation and block service startup. The migration service
/// logs any duplicates as warnings before this initializer runs.
/// </summary>
public sealed class AppealIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<AppealIndexInitializer> _logger;

    public AppealIndexInitializer(IMongoDatabase db, ILogger<AppealIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var appeals = _db.GetCollection<Appeal>(AppealRepositoryMongo.AppealsCollectionName);

        await appeals.Indexes.CreateOneAsync(
            new CreateIndexModel<Appeal>(
                Builders<Appeal>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Id),
                new CreateIndexOptions { Name = "ux_tenant_id", Unique = true }),
            cancellationToken: cancellationToken);

        await appeals.Indexes.CreateOneAsync(
            new CreateIndexModel<Appeal>(
                Builders<Appeal>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.AppealNumber),
                new CreateIndexOptions { Name = "ux_tenant_appeal_number", Unique = true }),
            cancellationToken: cancellationToken);

        await appeals.Indexes.CreateOneAsync(
            new CreateIndexModel<Appeal>(
                Builders<Appeal>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.ClaimId)
                    .Descending(x => x.CreatedAt),
                new CreateIndexOptions { Name = "ix_tenant_claim_created" }),
            cancellationToken: cancellationToken);

        await appeals.Indexes.CreateOneAsync(
            new CreateIndexModel<Appeal>(
                Builders<Appeal>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Status)
                    .Descending(x => x.CreatedAt),
                new CreateIndexOptions { Name = "ix_tenant_status_created" }),
            cancellationToken: cancellationToken);

        await appeals.Indexes.CreateOneAsync(
            new CreateIndexModel<Appeal>(
                Builders<Appeal>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.MemberId)
                    .Descending(x => x.CreatedAt),
                new CreateIndexOptions { Name = "ix_tenant_member_created" }),
            cancellationToken: cancellationToken);

        var events = _db.GetCollection<AppealEvent>(AppealEventRepositoryMongo.AppealEventsCollectionName);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<AppealEvent>(
                Builders<AppealEvent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.AppealId)
                    .Ascending(x => x.EventId),
                new CreateIndexOptions { Name = "ux_tenant_appeal_event", Unique = true }),
            cancellationToken: cancellationToken);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<AppealEvent>(
                Builders<AppealEvent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.AppealId)
                    .Ascending(x => x.OccurredAt),
                new CreateIndexOptions { Name = "ix_tenant_appeal_occurred" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Appeal, AppealEvent indexes ensured.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
