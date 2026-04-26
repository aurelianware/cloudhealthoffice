using BenefitPlanService.Models;
using Microsoft.Azure.Cosmos;
using MongoDB.Driver;

namespace BenefitPlanService.Repositories;

public interface IPlanVersionTransitionRepository
{
    Task<PlanVersionTransition> AppendAsync(PlanVersionTransition transition, CancellationToken ct = default);
    Task<IReadOnlyList<PlanVersionTransition>> ListAsync(string planId, string tenantId, CancellationToken ct = default);
}

public sealed class CosmosPlanVersionTransitionRepository : IPlanVersionTransitionRepository
{
    private readonly Container _container;

    public CosmosPlanVersionTransitionRepository(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"];
        var containerName = configuration["CosmosDb:PlanVersionTransitionsContainer"] ?? "PlanVersionTransitions";
        _container = client.GetContainer(databaseName, containerName);
    }

    public async Task<PlanVersionTransition> AppendAsync(PlanVersionTransition transition, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transition.Id)) transition.Id = Guid.NewGuid().ToString();
        var response = await _container.CreateItemAsync(transition, new PartitionKey(transition.TenantId), cancellationToken: ct);
        return response.Resource;
    }

    public async Task<IReadOnlyList<PlanVersionTransition>> ListAsync(string planId, string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.planId = @planId ORDER BY c.occurredAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId);

        var iterator = _container.GetItemQueryIterator<PlanVersionTransition>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<PlanVersionTransition>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results;
    }
}

public sealed class MongoPlanVersionTransitionRepository : IPlanVersionTransitionRepository
{
    private readonly IMongoCollection<PlanVersionTransition> _collection;

    public MongoPlanVersionTransitionRepository(IMongoDatabase database, IConfiguration configuration)
    {
        var collectionName = configuration["CosmosDb:PlanVersionTransitionsContainer"] ?? "PlanVersionTransitions";
        _collection = database.GetCollection<PlanVersionTransition>(collectionName);
    }

    public async Task<PlanVersionTransition> AppendAsync(PlanVersionTransition transition, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(transition.Id)) transition.Id = Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(transition, cancellationToken: ct);
        return transition;
    }

    public async Task<IReadOnlyList<PlanVersionTransition>> ListAsync(string planId, string tenantId, CancellationToken ct = default)
    {
        var b = Builders<PlanVersionTransition>.Filter;
        var filter = b.And(b.Eq(x => x.TenantId, tenantId), b.Eq(x => x.PlanId, planId));
        var docs = await _collection.Find(filter)
            .SortByDescending(x => x.OccurredAt)
            .ToListAsync(ct);
        return docs;
    }
}
