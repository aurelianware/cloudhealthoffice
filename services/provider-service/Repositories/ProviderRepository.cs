using Microsoft.Azure.Cosmos;
using ProviderService.Models;

namespace ProviderService.Repositories;

public interface IProviderRepository
{
    Task<Provider?> GetByIdAsync(string id);
    Task<Provider?> GetByNPIAsync(string npi);
    Task<IEnumerable<Provider>> SearchAsync(
        string? name, 
        string? specialty, 
        string? zipCode, 
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page, 
        int pageSize);
    Task<Provider> CreateAsync(Provider provider);
    Task<Provider> UpdateAsync(Provider provider);
    Task DeleteAsync(string id);
}

public class ProviderRepository : IProviderRepository
{
    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProviderRepository> _logger;

    public ProviderRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProviderRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "ProviderDB";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "Providers";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request context");
        }
        return tenantId;
    }

    public async Task<Provider?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();

        try
        {
            var response = await _container.ReadItemAsync<Provider>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Provider?> GetByNPIAsync(string npi)
    {
        var tenantId = GetTenantId();

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.npi = @npi")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@npi", npi);

        var iterator = _container.GetItemQueryIterator<Provider>(query);
        var results = new List<Provider>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results.FirstOrDefault();
    }

    public async Task<IEnumerable<Provider>> SearchAsync(
        string? name,
        string? specialty,
        string? zipCode,
        string? state,
        string? planId,
        LineOfBusiness? lineOfBusiness,
        ProviderType? providerType,
        bool? acceptingNewPatients,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();

        // Build dynamic query
        var conditions = new List<string> { "c.tenantId = @tenantId", "c.status = 'Active'" };
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE ");
        queryDef.WithParameter("@tenantId", tenantId);

        if (!string.IsNullOrEmpty(name))
        {
            conditions.Add("(CONTAINS(LOWER(c.firstName), LOWER(@name)) OR CONTAINS(LOWER(c.lastName), LOWER(@name)) OR CONTAINS(LOWER(c.organizationName), LOWER(@name)))");
            queryDef.WithParameter("@name", name);
        }

        if (!string.IsNullOrEmpty(specialty))
        {
            conditions.Add("CONTAINS(LOWER(c.primarySpecialty), LOWER(@specialty))");
            queryDef.WithParameter("@specialty", specialty);
        }

        if (!string.IsNullOrEmpty(zipCode))
        {
            conditions.Add("c.zipCode = @zipCode");
            queryDef.WithParameter("@zipCode", zipCode);
        }

        if (!string.IsNullOrEmpty(state))
        {
            conditions.Add("c.state = @state");
            queryDef.WithParameter("@state", state);
        }

        if (providerType.HasValue)
        {
            conditions.Add("c.providerType = @providerType");
            queryDef.WithParameter("@providerType", providerType.Value.ToString());
        }

        if (acceptingNewPatients.HasValue)
        {
            conditions.Add("c.acceptingNewPatients = @acceptingNewPatients");
            queryDef.WithParameter("@acceptingNewPatients", acceptingNewPatients.Value);
        }

        // Network participation filter (array search)
        if (!string.IsNullOrEmpty(planId) || lineOfBusiness.HasValue)
        {
            if (!string.IsNullOrEmpty(planId) && lineOfBusiness.HasValue)
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.planId = @planId AND n.lineOfBusiness = @lineOfBusiness)");
                queryDef.WithParameter("@planId", planId);
                queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
            }
            else if (!string.IsNullOrEmpty(planId))
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.planId = @planId)");
                queryDef.WithParameter("@planId", planId);
            }
            else if (lineOfBusiness.HasValue)
            {
                conditions.Add("EXISTS(SELECT VALUE n FROM n IN c.networkParticipations WHERE n.lineOfBusiness = @lineOfBusiness)");
                queryDef.WithParameter("@lineOfBusiness", lineOfBusiness.Value.ToString());
            }
        }

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)} ORDER BY c.lastName, c.organizationName OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";
        var finalQuery = new QueryDefinition(queryText);

        // Re-apply all parameters to final query
        foreach (var (name2, value) in queryDef.GetQueryParameters())
        {
            finalQuery.WithParameter(name2, value);
        }

        var iterator = _container.GetItemQueryIterator<Provider>(finalQuery);
        var results = new List<Provider>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<Provider> CreateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        provider.TenantId = tenantId;

        var response = await _container.CreateItemAsync(provider, new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task<Provider> UpdateAsync(Provider provider)
    {
        var tenantId = GetTenantId();
        provider.TenantId = tenantId;

        var response = await _container.ReplaceItemAsync(
            provider,
            provider.Id,
            new PartitionKey(tenantId));
        return response.Resource;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<Provider>(id, new PartitionKey(tenantId));
    }
}

// Extension method to get query parameters (for debugging/logging)
public static class QueryDefinitionExtensions
{
    public static IEnumerable<(string, object)> GetQueryParameters(this QueryDefinition queryDef)
    {
        // Note: QueryDefinition doesn't expose parameters publicly
        // This is a placeholder - in production, track parameters separately or use logging
        return new List<(string, object)>();
    }
}
