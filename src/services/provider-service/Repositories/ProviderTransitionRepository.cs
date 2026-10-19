using ProviderService.Models;
using MongoDB.Driver;

namespace ProviderService.Repositories;

public interface IProviderTransitionRepository
{
    Task<ProviderTransition> AppendAsync(ProviderTransition transition, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderTransition>> ListAsync(string providerId, string tenantId, CancellationToken ct = default);
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
