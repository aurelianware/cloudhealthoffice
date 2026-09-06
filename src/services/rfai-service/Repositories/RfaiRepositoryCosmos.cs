using Microsoft.Azure.Cosmos;
using RfaiService.Models;

namespace RfaiService.Repositories;

public class RfaiRepositoryCosmos : IRfaiRepository
{
    private readonly Container _container;
    private readonly ILogger<RfaiRepositoryCosmos> _logger;

    public RfaiRepositoryCosmos(CosmosClient cosmosClient, IConfiguration configuration, ILogger<RfaiRepositoryCosmos> logger)
    {
        var database  = configuration["CosmosDb:DatabaseName"]  ?? "CloudHealthOffice";
        var container = configuration["CosmosDb:RfaiContainer"] ?? "RfaiCases";
        _container = cosmosClient.GetContainer(database, container);
        _logger = logger;
    }

    public async Task<RfaiCase?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<RfaiCase>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.authNumber = @authNumber ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@authNumber", authNumber);

        var results = new List<RfaiCase>();
        using var iterator = _container.GetItemQueryIterator<RfaiCase>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            results.AddRange(page);
        }

        return results;
    }

    public async Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.trackingId = @trackingId")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@trackingId", trackingId);

        using var iterator = _container.GetItemQueryIterator<RfaiCase>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = 1,
            });

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            var match = page.FirstOrDefault();
            if (match is not null) return match;
        }

        return null;
    }

    public async Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
    {
        var response = await _container.CreateItemAsync(rfaiCase, new PartitionKey(rfaiCase.TenantId));
        return response.Resource;
    }

    /// <inheritdoc />
    public async Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase)
    {
        try
        {
            var response = await _container.CreateItemAsync(
                rfaiCase, new PartitionKey(rfaiCase.TenantId));
            return (response.Resource, true);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Someone already created the case this event addresses. Read theirs
            // rather than creating a second active request for one decision.
            var existing = await GetByIdAsync(rfaiCase.TenantId, rfaiCase.Id);
            if (existing is not null)
                return (existing, false);

            _logger.LogWarning(
                "RFAI create conflicted but the case could not be read back: {Id}", rfaiCase.Id);
            throw;
        }
    }

    public async Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
    {
        rfaiCase.UpdatedAt = DateTime.UtcNow;
        var response = await _container.ReplaceItemAsync(rfaiCase, rfaiCase.Id, new PartitionKey(rfaiCase.TenantId));
        return response.Resource;
    }
}
