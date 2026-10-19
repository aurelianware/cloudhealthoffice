using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.Repositories;

public interface IPlanVersionTransitionRepository
{
    Task<PlanVersionTransition> AppendAsync(PlanVersionTransition transition, CancellationToken ct = default);
    Task<IReadOnlyList<PlanVersionTransition>> ListAsync(string planId, string tenantId, CancellationToken ct = default);
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
