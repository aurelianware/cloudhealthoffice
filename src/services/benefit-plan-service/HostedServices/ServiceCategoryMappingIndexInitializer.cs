using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Persistence;
using MongoDB.Driver;

namespace BenefitPlanService.HostedServices;

/// <summary>
/// Creates the index required by newest-first service-category mapping reads
/// when the Mongo endpoint is backed by Azure Cosmos DB.
/// </summary>
public sealed class ServiceCategoryMappingIndexInitializer : IHostedService
{
    private readonly IMongoDatabase _database;
    private readonly string _collectionName;
    private readonly ILogger<ServiceCategoryMappingIndexInitializer> _logger;

    public ServiceCategoryMappingIndexInitializer(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<ServiceCategoryMappingIndexInitializer> logger)
    {
        _database = database;
        _collectionName = configuration["CosmosDb:ServiceCategoryMappingsContainerName"]
            ?? ChoServiceCategoryMappingRepository.DefaultContainerName;
        _logger = logger;
    }

    internal static IReadOnlyList<CreateIndexModel<ChoServiceCategoryMappingRepositoryMongo.MappingDocument>>
        BuildIndexes() =>
        [
            new(
                Builders<ChoServiceCategoryMappingRepositoryMongo.MappingDocument>.IndexKeys
                    .Descending(x => x.CreatedAt),
                new CreateIndexOptions { Name = "ix_service_category_mappings_createdAt_desc" })
        ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var collection =
            _database.GetCollection<ChoServiceCategoryMappingRepositoryMongo.MappingDocument>(_collectionName);
        await collection.Indexes.CreateManyAsync(BuildIndexes(), cancellationToken);

        _logger.LogInformation(
            "Service-category mapping query indexes ensured on collection '{Collection}'.",
            _collectionName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
