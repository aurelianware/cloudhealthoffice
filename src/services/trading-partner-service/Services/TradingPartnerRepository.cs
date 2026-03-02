using Microsoft.Azure.Cosmos;
using CloudHealthOffice.TradingPartnerService.Models;

namespace CloudHealthOffice.TradingPartnerService.Services;

public interface ITradingPartnerRepository
{
    Task<TradingPartner?> GetAsync(string tenantId, string tradingPartnerId, string environment);
    Task<IEnumerable<TradingPartner>> GetByTenantAsync(string tenantId);
    Task<TradingPartner> CreateAsync(TradingPartner partner);
    Task<TradingPartner> UpdateAsync(TradingPartner partner);
    Task DeleteAsync(string id, string partitionKey);
}

public class TradingPartnerRepository : ITradingPartnerRepository
{
    private readonly Container _container;
    private readonly ILogger<TradingPartnerRepository> _logger;

    public TradingPartnerRepository(CosmosClient cosmosClient, ILogger<TradingPartnerRepository> logger)
    {
        var databaseName = Environment.GetEnvironmentVariable("COSMOS_DATABASE") ?? "CloudHealthOffice";
        var containerName = Environment.GetEnvironmentVariable("COSMOS_CONTAINER_TRADING_PARTNERS") ?? "TradingPartners";

        _container = cosmosClient.GetContainer(databaseName, containerName);
        _logger = logger;
    }

    public async Task<TradingPartner?> GetAsync(string tenantId, string tradingPartnerId, string environment)
    {
        var id = $"{tradingPartnerId}-{tenantId}-{environment}";
        
        try
        {
            var response = await _container.ReadItemAsync<TradingPartner>(
                id,
                new PartitionKey(tenantId));

            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "Trading partner not found: {TenantId}/{TradingPartnerId}/{Environment}",
                SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment));
            return null;
        }
    }

    public async Task<IEnumerable<TradingPartner>> GetByTenantAsync(string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId")
            .WithParameter("@tenantId", tenantId);

        var iterator = _container.GetItemQueryIterator<TradingPartner>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        var results = new List<TradingPartner>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<TradingPartner> CreateAsync(TradingPartner partner)
    {
        var response = await _container.CreateItemAsync(
            partner,
            new PartitionKey(partner.TenantId));

        _logger.LogInformation(
            "Created trading partner: {Id} in partition {TenantId}",
            SanitizeForLog(partner.Id), SanitizeForLog(partner.TenantId));

        return response.Resource;
    }

    public async Task<TradingPartner> UpdateAsync(TradingPartner partner)
    {
        var response = await _container.ReplaceItemAsync(
            partner,
            partner.Id,
            new PartitionKey(partner.TenantId));

        _logger.LogInformation(
            "Updated trading partner: {Id}",
            SanitizeForLog(partner.Id));

        return response.Resource;
    }

    public async Task DeleteAsync(string id, string partitionKey)
    {
        await _container.DeleteItemAsync<TradingPartner>(
            id,
            new PartitionKey(partitionKey));

        _logger.LogWarning(
            "Deleted trading partner: {Id} from partition {PartitionKey}",
            SanitizeForLog(id), SanitizeForLog(partitionKey));
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove newline characters to prevent log forging via line injection.
        return value
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
    }
}
