using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;
using MongoDB.Driver;

namespace PersonalRepresentativeService.HostedServices;

/// <summary>
/// Creates the Mongo indexes used by the PersonalRepresentatives,
/// PersonalRepAssociations, and PersonalRepEvents collections once at
/// startup. Running from a hosted service (rather than the repository
/// constructor) keeps construction side-effect free and lets us register
/// repositories as singletons.
///
/// Idempotent: Mongo silently no-ops an index that already exists with the
/// same spec.
/// </summary>
public sealed class PersonalRepIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _db;
    private readonly ILogger<PersonalRepIndexInitializer> _logger;

    public PersonalRepIndexInitializer(IMongoDatabase db, ILogger<PersonalRepIndexInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var reps = _db.GetCollection<PersonalRepresentative>(PersonalRepRepositoryMongo.PersonalRepsCollectionName);

        await reps.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepresentative>(
                Builders<PersonalRepresentative>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Id),
                new CreateIndexOptions { Name = "ux_tenant_id", Unique = true }),
            cancellationToken: cancellationToken);

        await reps.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepresentative>(
                Builders<PersonalRepresentative>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Status)
                    .Descending(x => x.CreatedAt),
                new CreateIndexOptions { Name = "ix_tenant_status_created" }),
            cancellationToken: cancellationToken);

        var associations = _db.GetCollection<PersonalRepAssociation>(
            PersonalRepRepositoryMongo.PersonalRepAssociationsCollectionName);

        await associations.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepAssociation>(
                Builders<PersonalRepAssociation>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.Id),
                new CreateIndexOptions { Name = "ux_tenant_id", Unique = true }),
            cancellationToken: cancellationToken);

        await associations.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepAssociation>(
                Builders<PersonalRepAssociation>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.PairId),
                new CreateIndexOptions { Name = "ix_tenant_pair" }),
            cancellationToken: cancellationToken);

        await associations.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepAssociation>(
                Builders<PersonalRepAssociation>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.MemberId)
                    .Ascending(x => x.Direction),
                new CreateIndexOptions { Name = "ix_tenant_member_direction" }),
            cancellationToken: cancellationToken);

        await associations.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepAssociation>(
                Builders<PersonalRepAssociation>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.RepId)
                    .Ascending(x => x.Direction),
                new CreateIndexOptions { Name = "ix_tenant_rep_direction" }),
            cancellationToken: cancellationToken);

        await associations.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepAssociation>(
                Builders<PersonalRepAssociation>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.RepId)
                    .Ascending(x => x.MemberId),
                new CreateIndexOptions { Name = "ix_tenant_rep_member" }),
            cancellationToken: cancellationToken);

        var events = _db.GetCollection<PersonalRepEvent>(
            PersonalRepEventRepositoryMongo.PersonalRepEventsCollectionName);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepEvent>(
                Builders<PersonalRepEvent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.PersonalRepId)
                    .Ascending(x => x.EventId),
                new CreateIndexOptions { Name = "ux_tenant_rep_event", Unique = true }),
            cancellationToken: cancellationToken);

        await events.Indexes.CreateOneAsync(
            new CreateIndexModel<PersonalRepEvent>(
                Builders<PersonalRepEvent>.IndexKeys
                    .Ascending(x => x.TenantId)
                    .Ascending(x => x.PersonalRepId)
                    .Ascending(x => x.OccurredAt),
                new CreateIndexOptions { Name = "ix_tenant_rep_occurred" }),
            cancellationToken: cancellationToken);

        _logger.LogInformation("PersonalRepresentative, PersonalRepAssociation, PersonalRepEvent indexes ensured.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
