using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.HostedServices;

/// <summary>
/// Creates indexes required by sorted BenefitPlans queries. Azure Cosmos DB
/// for MongoDB rejects an ORDER BY when the corresponding index path is
/// absent, while local MongoDB can satisfy the same query with an in-memory
/// sort.
/// </summary>
public sealed class BenefitPlanIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _database;
    private readonly string _collectionName;
    private readonly ILogger<BenefitPlanIndexInitializer> _logger;

    public BenefitPlanIndexInitializer(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<BenefitPlanIndexInitializer> logger)
    {
        _database = database;
        _collectionName = configuration["CosmosDb:ContainerName"] ?? "BenefitPlans";
        _logger = logger;
    }

    internal static IReadOnlyList<CreateIndexModel<BenefitPlan>> BuildIndexes() =>
        new[]
        {
            new CreateIndexModel<BenefitPlan>(
                Builders<BenefitPlan>.IndexKeys.Descending(x => x.VersionNumber),
                new CreateIndexOptions { Name = "ix_benefitplans_VersionNumber_desc" }),
            new CreateIndexModel<BenefitPlan>(
                Builders<BenefitPlan>.IndexKeys.Ascending(x => x.PlanName),
                new CreateIndexOptions { Name = "ix_benefitplans_PlanName_asc" })
        };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var collection = _database.GetCollection<BenefitPlan>(_collectionName);
        await collection.Indexes.CreateManyAsync(BuildIndexes(), cancellationToken);

        _logger.LogInformation(
            "BenefitPlan query indexes ensured on collection '{Collection}'.",
            _collectionName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
