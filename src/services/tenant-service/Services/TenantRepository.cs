using Microsoft.Azure.Cosmos;
using TenantService.Models;

namespace TenantService.Services;

public class TenantRepository : ITenantRepository
{
    private readonly Container _container;
    private readonly ILogger<TenantRepository> _logger;

    public TenantRepository(CosmosClient cosmosClient, IConfiguration configuration, ILogger<TenantRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:TenantContainerName"] ?? "Tenants";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<Tenant?> GetByIdAsync(string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<Tenant>(id, new PartitionKey(id));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Tenant with ID {TenantId} not found", id);
            return null;
        }
    }

    public async Task<Tenant?> GetByTenantIdAsync(string tenantId)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @tenantId")
            .WithParameter("@tenantId", tenantId);

        var iterator = _container.GetItemQueryIterator<Tenant>(query);
        var response = await iterator.ReadNextAsync();

        return response.FirstOrDefault();
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync(int pageSize = 100, string? continuationToken = null)
    {
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.createdAt DESC");
        var requestOptions = new QueryRequestOptions { MaxItemCount = pageSize };

        var iterator = _container.GetItemQueryIterator<Tenant>(query, continuationToken, requestOptions);
        var tenants = new List<Tenant>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            tenants.AddRange(response);
            break; // Only get first page for now
        }

        return tenants;
    }

    public async Task<IEnumerable<Tenant>> GetByStatusAsync(string status)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", status);

        var iterator = _container.GetItemQueryIterator<Tenant>(query);
        var tenants = new List<Tenant>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            tenants.AddRange(response);
        }

        return tenants;
    }

    public async Task<Tenant> CreateAsync(Tenant tenant)
    {
        tenant.CreatedAt = DateTime.UtcNow;
        tenant.UpdatedAt = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(tenant, new PartitionKey(tenant.Id));
        _logger.LogInformation("Created tenant {TenantId} ({TenantName})", tenant.TenantId, tenant.TenantName);
        
        return response.Resource;
    }

    public async Task<Tenant> UpdateAsync(Tenant tenant)
    {
        tenant.UpdatedAt = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(tenant, tenant.Id, new PartitionKey(tenant.Id));
        _logger.LogInformation("Updated tenant {TenantId}", tenant.TenantId);
        
        return response.Resource;
    }

    public async Task DeleteAsync(string tenantId)
    {
        var tenant = await GetByTenantIdAsync(tenantId);
        if (tenant != null)
        {
            await _container.DeleteItemAsync<Tenant>(tenant.Id, new PartitionKey(tenant.Id));
            _logger.LogInformation("Deleted tenant {TenantId}", tenantId);
        }
    }

    public async Task<bool> ExistsAsync(string tenantId)
    {
        var tenant = await GetByTenantIdAsync(tenantId);
        return tenant != null;
    }

    public async Task<Tenant?> GetByApiKeyHashAsync(string keyHash)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE ARRAY_CONTAINS(c.apiKeys, {'keyHash': @keyHash}, true)")
            .WithParameter("@keyHash", keyHash);

        var iterator = _container.GetItemQueryIterator<Tenant>(query);
        
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            var tenant = response.FirstOrDefault(t => 
                t.ApiKeys.Any(k => k.KeyHash == keyHash && k.IsActive));
            
            if (tenant != null)
                return tenant;
        }

        return null;
    }
}
