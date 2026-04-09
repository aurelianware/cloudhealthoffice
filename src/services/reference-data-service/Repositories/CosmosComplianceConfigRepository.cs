using Microsoft.Azure.Cosmos;
using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

/// <summary>
/// Cosmos DB-backed repository for <see cref="TenantComplianceConfig"/> documents.
/// Container: <c>compliance-configs</c>, partition key: <c>/tenantId</c>.
/// The document <c>id</c> is set to the tenantId for direct O(1) point-read/upsert.
/// </summary>
public class CosmosComplianceConfigRepository : IComplianceConfigRepository
{
    private readonly Container _container;

    public CosmosComplianceConfigRepository(CosmosClient cosmosClient, IConfiguration configuration)
    {
        var dbName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(dbName, "compliance-configs");
    }

    public async Task<TenantComplianceConfig?> GetAsync(string tenantId)
    {
        try
        {
            var response = await _container.ReadItemAsync<TenantComplianceConfig>(
                tenantId, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<TenantComplianceConfig> UpsertAsync(TenantComplianceConfig config)
    {
        // Use tenantId as the document ID for deterministic point reads/upserts.
        config.Id = config.TenantId;

        var response = await _container.UpsertItemAsync(
            config, new PartitionKey(config.TenantId));
        return response.Resource;
    }
}
