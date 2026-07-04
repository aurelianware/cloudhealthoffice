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

        var claimResults = _database.GetCollection<MassAdjudicationClaimResult>(
            MassAdjudicationRunRepositoryMongo.ClaimResultsCollectionName);
        var claimResultKeys = Builders<MassAdjudicationClaimResult>.IndexKeys;

        claimResults.Indexes.CreateMany(
            new[]
            {
                new CreateIndexModel<MassAdjudicationClaimResult>(
                    claimResultKeys.Ascending(x => x.TenantId).Ascending(x => x.RunId).Ascending(x => x.Outcome),
                    new CreateIndexOptions { Name = "tenant_run_outcome" }),
                new CreateIndexModel<MassAdjudicationClaimResult>(
                    claimResultKeys.Ascending(x => x.TenantId).Ascending(x => x.RunId).Descending(x => x.ElapsedMilliseconds),
                    new CreateIndexOptions { Name = "tenant_run_elapsed_desc" }),
                new CreateIndexModel<MassAdjudicationClaimResult>(
                    claimResultKeys.Ascending(x => x.TenantId).Ascending(x => x.SubmittedClaimId),
                    new CreateIndexOptions
                    {
                        Name = "tenant_submitted_claim",
                        Sparse = true
                    })
            },
            cancellationToken);

        _logger.LogInformation(
            "Mass adjudication run indexes ensured on collections '{RunCollection}' and '{ClaimResultCollection}'.",
            MassAdjudicationRunRepositoryMongo.CollectionName,
            MassAdjudicationRunRepositoryMongo.ClaimResultsCollectionName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
