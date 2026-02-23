using Microsoft.Azure.Cosmos;
using BenefitPlanService.Models;

namespace BenefitPlanService.Repositories;

public interface IBenefitPlanRepository
{
    Task<BenefitPlan?> GetByIdAsync(string id, string tenantId);
    Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId);
    Task<IEnumerable<BenefitPlan>> SearchAsync(string tenantId, string? lineOfBusiness, string? planType, string? metalLevel, int page, int pageSize);
    Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory);
    Task<BenefitPlan> CreateAsync(BenefitPlan plan);
    Task<BenefitPlan> UpdateAsync(BenefitPlan plan);
    Task DeleteAsync(string id, string tenantId);
}

public class BenefitPlanRepository : IBenefitPlanRepository
{
    private readonly Container _container;
    private readonly ILogger<BenefitPlanRepository> _logger;

    public BenefitPlanRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<BenefitPlanRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"];
        var containerName = configuration["CosmosDb:ContainerName"];
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<BenefitPlan?> GetByIdAsync(string id, string tenantId)
    {
        try
        {
            var response = await _container.ReadItemAsync<BenefitPlan>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<BenefitPlan?> GetByPlanIdAsync(string planId, string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.planId = @planId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@planId", planId);

        var iterator = _container.GetItemQueryIterator<BenefitPlan>(query);
        var results = new List<BenefitPlan>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<BenefitPlan>> SearchAsync(
        string tenantId,
        string? lineOfBusiness,
        string? planType,
        string? metalLevel,
        int page,
        int pageSize)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId);

        if (!string.IsNullOrEmpty(lineOfBusiness))
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@lineOfBusiness", lineOfBusiness);
        }

        if (!string.IsNullOrEmpty(planType))
        {
            queryText += " AND c.planType = @planType";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId);
            if (!string.IsNullOrEmpty(lineOfBusiness))
                queryDef = queryDef.WithParameter("@lineOfBusiness", lineOfBusiness);
            queryDef = queryDef.WithParameter("@planType", planType);
        }

        if (!string.IsNullOrEmpty(metalLevel))
        {
            queryText += " AND c.metalLevel = @metalLevel";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId);
            if (!string.IsNullOrEmpty(lineOfBusiness))
                queryDef = queryDef.WithParameter("@lineOfBusiness", lineOfBusiness);
            if (!string.IsNullOrEmpty(planType))
                queryDef = queryDef.WithParameter("@planType", planType);
            queryDef = queryDef.WithParameter("@metalLevel", metalLevel);
        }

        queryText += " ORDER BY c.planName OFFSET @offset LIMIT @limit";
        queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId);
        if (!string.IsNullOrEmpty(lineOfBusiness))
            queryDef = queryDef.WithParameter("@lineOfBusiness", lineOfBusiness);
        if (!string.IsNullOrEmpty(planType))
            queryDef = queryDef.WithParameter("@planType", planType);
        if (!string.IsNullOrEmpty(metalLevel))
            queryDef = queryDef.WithParameter("@metalLevel", metalLevel);
        queryDef = queryDef
            .WithParameter("@offset", (page - 1) * pageSize)
            .WithParameter("@limit", pageSize);

        var iterator = _container.GetItemQueryIterator<BenefitPlan>(queryDef);
        var results = new List<BenefitPlan>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<IEnumerable<Benefit>> GetBenefitsAsync(string planId, string tenantId, string? serviceCategory)
    {
        var plan = await GetByPlanIdAsync(planId, tenantId);
        if (plan == null)
        {
            return Enumerable.Empty<Benefit>();
        }

        var benefits = plan.Benefits ?? new List<Benefit>();

        if (!string.IsNullOrEmpty(serviceCategory))
        {
            benefits = benefits.Where(b => b.ServiceCategory == serviceCategory).ToList();
        }

        return benefits;
    }

    public async Task<BenefitPlan> CreateAsync(BenefitPlan plan)
    {
        plan.Id = Guid.NewGuid().ToString();
        plan.CreatedDate = DateTime.UtcNow;
        plan.ModifiedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            plan,
            new PartitionKey(plan.TenantId));

        return response.Resource;
    }

    public async Task<BenefitPlan> UpdateAsync(BenefitPlan plan)
    {
        plan.ModifiedDate = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(
            plan,
            plan.Id,
            new PartitionKey(plan.TenantId));

        return response.Resource;
    }

    public async Task DeleteAsync(string id, string tenantId)
    {
        await _container.DeleteItemAsync<BenefitPlan>(
            id,
            new PartitionKey(tenantId));
    }
}
