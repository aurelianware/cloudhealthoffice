using ClaimsService.Models;
using ClaimsService.Repositories;
using MongoDB.Driver;

namespace ClaimsService.HostedServices;

public sealed class MassAdjudicationRunIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<MassAdjudicationRunIndexInitializer> _logger;

    public MassAdjudicationRunIndexInitializer(
        IMongoDatabase database,
        ILogger<MassAdjudicationRunIndexInitializer> logger)
    {
        _database = database;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<MassAdjudicationRunSummary>(
            MassAdjudicationRunRepositoryMongo.CollectionName);
        var keys = Builders<MassAdjudicationRunSummary>.IndexKeys;

        collection.Indexes.CreateMany(
            new[]
            {
                new CreateIndexModel<MassAdjudicationRunSummary>(
                    keys.Ascending(x => x.Run.TenantId).Descending(x => x.Run.StartedAtUtc),
                    new CreateIndexOptions { Name = "tenant_started_desc" }),
                new CreateIndexModel<MassAdjudicationRunSummary>(
                    keys.Ascending(x => x.Run.TenantId).Ascending(x => x.Id),
                    new CreateIndexOptions { Name = "tenant_run_id" })
            },
            cancellationToken);

        _logger.LogInformation(
            "Mass adjudication run indexes ensured on collection '{Collection}'.",
            MassAdjudicationRunRepositoryMongo.CollectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
