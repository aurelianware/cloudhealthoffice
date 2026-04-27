using ProviderService.Models;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

namespace ProviderService.Repositories;

public interface IProviderTransitionRepository
{
    Task<ProviderTransition> AppendAsync(ProviderTransition transition, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderTransition>> ListAsync(string providerId, string tenantId, CancellationToken ct = default);
}

public sealed class CosmosProviderTransitionRepository : IProviderTransitionRepository
{
    private readonly Container _container;

    public CosmosProviderTransitionRepository(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ProviderDB";
        var containerName = configuration["CosmosDb:ProviderTransitionsContainer"] ?? "ProviderTransitions";
        _container = client.GetContainer(databaseName, containerName);
    }

    public async Task<ProviderTransition> AppendAsync(ProviderTransition transition, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transition.Id)) transition.Id = Guid.NewGuid().ToString();
        var response = await _container.CreateItemAsync(transition, new PartitionKey(transition.TenantId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<IReadOnlyList<ProviderTransition>> ListAsync(string providerId, string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.providerId = @providerId ORDER BY c.occurredAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@providerId", providerId);

        var iterator = _container.GetItemQueryIterator<ProviderTransition>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<ProviderTransition>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }
}

public sealed class MongoProviderTransitionRepository : IProviderTransitionRepository
{
    private readonly IMongoCollection<ProviderTransition> _collection;

    public MongoProviderTransitionRepository(IMongoDatabase database, IConfiguration configuration)
    {
        var collectionName = configuration["CosmosDb:ProviderTransitionsContainer"] ?? "ProviderTransitions";
        _collection = database.GetCollection<ProviderTransition>(collectionName);
    }

    public async Task<ProviderTransition> AppendAsync(ProviderTransition transition, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transition.Id)) transition.Id = Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(transition, cancellationToken: ct);
        return transition;
    }

    public async Task<IReadOnlyList<ProviderTransition>> ListAsync(string providerId, string tenantId, CancellationToken ct = default)
    {
        var b = Builders<ProviderTransition>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.ProviderId, providerId));
        var docs = await _collection.Find(filter)
            .SortByDescending(x => x.OccurredAt)
            .ToListAsync(ct);
        return docs;
    }
}
