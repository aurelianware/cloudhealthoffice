using BenefitPlanService.Models;
using MongoDB.Driver;

namespace BenefitPlanService.Repositories;

public class BenefitPlanRepositoryMongo : IBenefitPlanRepository
{
    private readonly IMongoCollection<BenefitPlan> _collection;
    private readonly ILogger<BenefitPlanRepositoryMongo> _logger;

    public BenefitPlanRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<BenefitPlanRepositoryMongo> logger)
    {
        var collectionName = configuration["CosmosDb:ContainerName"] ?? "BenefitPlans";
        _collection = database.GetCollection<BenefitPlan>(collectionName);
        _logger = logger;
    }

    public async Task<BenefitPlan?> GetByIdAsync(string id, string tenantId)
    {
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId)
    {
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.PlanId, planId),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<BenefitPlan>> SearchAsync(
        string tenantId,
        string? lineOfBusiness,
        string? planType,
        string? metalLevel,
        int page,
        int pageSize)
    {
        var filter = Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId);

        if (!string.IsNullOrEmpty(lineOfBusiness) && Enum.TryParse<LineOfBusiness>(lineOfBusiness, true, out var lob))
        {
            filter &= Builders<BenefitPlan>.Filter.Eq(x => x.LineOfBusiness, lob);
        }

        if (!string.IsNullOrEmpty(planType) && Enum.TryParse<PlanType>(planType, true, out var pt))
        {
            filter &= Builders<BenefitPlan>.Filter.Eq(x => x.PlanType, pt);
        }

        if (!string.IsNullOrEmpty(metalLevel) && Enum.TryParse<MetalLevel>(metalLevel, true, out var ml))
        {
            filter &= Builders<BenefitPlan>.Filter.Eq(x => x.MetalLevel, ml);
        }

        return await _collection.Find(filter)
            .SortBy(x => x.PlanName)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory)
    {
        var plan = await GetByPlanIdAsync(planId, tenantId);
        if (plan == null || plan.Benefits == null)
        {
            return Enumerable.Empty<Benefit>();
        }

        var benefits = plan.Benefits.AsEnumerable();

        if (!string.IsNullOrEmpty(serviceCategory))
        {
            benefits = benefits.Where(b => b.ServiceCategory == serviceCategory);
        }

        return benefits;
    }

    public async Task<BenefitPlan> CreateAsync(BenefitPlan plan)
    {
        plan.Id ??= Guid.NewGuid().ToString();
        await _collection.InsertOneAsync(plan);
        return plan;
    }

    public async Task<BenefitPlan> UpdateAsync(BenefitPlan plan)
    {
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, plan.Id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, plan.TenantId)
        );
        await _collection.ReplaceOneAsync(filter, plan);
        return plan;
    }

    public async Task DeleteAsync(string id, string tenantId)
    {
        var filter = Builders<BenefitPlan>.Filter.And(
            Builders<BenefitPlan>.Filter.Eq(x => x.Id, id),
            Builders<BenefitPlan>.Filter.Eq(x => x.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
    }
}
