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
        RequireTenant(tenantId);
        // By the record's fields within the tenant's partition, not a recomputed id:
        // records keep whichever id they were saved with (see TradingPartnerIds).
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.tradingPartnerId = @partnerId AND c.environment = @environment")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@partnerId", tradingPartnerId)
            .WithParameter("@environment", environment);

        using var iterator = _container.GetItemQueryIterator<TradingPartner>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId), MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var found = page.FirstOrDefault();
            if (found != null)
                return found;
        }

        _logger.LogWarning(
            "Trading partner not found: {TenantId}/{TradingPartnerId}/{Environment}",
            SanitizeForLog(tenantId), SanitizeForLog(tradingPartnerId), SanitizeForLog(environment));
        return null;
    }

    public async Task<IEnumerable<TradingPartner>> GetByTenantAsync(string tenantId)
    {
        RequireTenant(tenantId);
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

    /// <summary>
    /// Creates the record. A record with the same (tenant, partner id,
    /// environment) is refused with <see cref="DuplicateTradingPartnerException"/>:
    /// a record saved under the old id is found by the lookup first, and the
    /// deterministic id (unique within the tenant's partition) refuses a
    /// concurrent second create.
    /// </summary>
    public async Task<TradingPartner> CreateAsync(TradingPartner partner)
    {
        RequireTenant(partner.TenantId);
        if (await GetAsync(partner.TenantId, partner.TradingPartnerId, partner.Environment) != null)
            throw new DuplicateTradingPartnerException(partner.TradingPartnerId, partner.Environment);

        ItemResponse<TradingPartner> response;
        try
        {
            response = await _container.CreateItemAsync(
                partner,
                new PartitionKey(partner.TenantId));
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new DuplicateTradingPartnerException(partner.TradingPartnerId, partner.Environment, ex);
        }

        _logger.LogInformation(
            "Created trading partner: {Id} in partition {TenantId}",
            SanitizeForLog(partner.Id), SanitizeForLog(partner.TenantId));

        return response.Resource;
    }

    public async Task<TradingPartner> UpdateAsync(TradingPartner partner)
    {
        RequireTenant(partner.TenantId);
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
        RequireTenant(partitionKey);
        await _container.DeleteItemAsync<TradingPartner>(
            id,
            new PartitionKey(partitionKey));

        _logger.LogWarning(
            "Deleted trading partner: {Id} from partition {PartitionKey}",
            SanitizeForLog(id), SanitizeForLog(partitionKey));
    }

    private static string RequireTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId)
            ? throw new InvalidOperationException("A tenant is required; trading partners are never read or written without one.")
            : tenantId;

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
