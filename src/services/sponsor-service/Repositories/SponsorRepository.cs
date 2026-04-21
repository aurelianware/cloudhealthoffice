using Microsoft.Azure.Cosmos;
using SponsorService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SponsorService.Repositories;

/// <summary>
/// Cosmos DB repository for Sponsor entities.
/// Uses TenantId as partition key for multi-tenant isolation.
/// </summary>
public class SponsorRepository : ISponsorRepository
{
    private readonly Container _container;
    private const string ContainerName = "Sponsors";
    private const string PartitionKeyPath = "/tenantId";

    public SponsorRepository(CosmosClient cosmosClient, string databaseName)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        _container = database.GetContainer(ContainerName);
    }

    public async Task<Sponsor?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<Sponsor>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Sponsor?> GetByGroupNumberAsync(string tenantId, string groupNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        var iterator = _container.GetItemQueryIterator<Sponsor>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return null;
    }

    public async Task<(IEnumerable<Sponsor> Items, string? ContinuationToken, int TotalCount)> GetPagedAsync(
        string tenantId,
        SponsorStatus? status = null,
        bool activeOnly = false,
        LineOfBusiness? lineOfBusiness = null,
        int pageSize = 20,
        string? continuationToken = null)
    {
        // Build the query predicate incrementally so the LOB filter is applied
        // by Cosmos rather than in-memory after paging (preserves correct
        // TotalCount + continuation semantics under a LOB filter).
        var conditions = new List<string> { "c.tenantId = @tenantId" };
        var parameters = new Dictionary<string, object> { ["@tenantId"] = tenantId };

        if (activeOnly)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = (int)SponsorStatus.Active;
        }
        else if (status.HasValue)
        {
            conditions.Add("c.status = @status");
            parameters["@status"] = (int)status.Value;
        }

        if (lineOfBusiness.HasValue)
        {
            conditions.Add("c.lineOfBusiness = @lineOfBusiness");
            parameters["@lineOfBusiness"] = (int)lineOfBusiness.Value;
        }

        var queryText = "SELECT * FROM c WHERE " + string.Join(" AND ", conditions);
        var queryDef = new QueryDefinition(queryText);
        foreach (var (k, v) in parameters) queryDef.WithParameter(k, v);

        var iterator = _container.GetItemQueryIterator<Sponsor>(
            queryDef,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = pageSize
            });

        var results = new List<Sponsor>();
        string? newContinuationToken = null;

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
            newContinuationToken = response.ContinuationToken;
        }

        // Note: Cosmos DB doesn't provide total count efficiently in paginated queries
        // For accurate count, you'd need a separate COUNT query
        var totalCount = results.Count;

        return (results, newContinuationToken, totalCount);
    }

    public async Task<Sponsor> CreateAsync(Sponsor sponsor)
    {
        sponsor.CreatedDate = DateTime.UtcNow;
        sponsor.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            sponsor,
            new PartitionKey(sponsor.TenantId));

        return response.Resource;
    }

    public async Task<Sponsor> UpdateAsync(Sponsor sponsor)
    {
        sponsor.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(
            sponsor,
            sponsor.Id,
            new PartitionKey(sponsor.TenantId));

        return response.Resource;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        await _container.DeleteItemAsync<Sponsor>(
            id,
            new PartitionKey(tenantId));
    }

    public async Task<bool> ExistsAsync(string tenantId, string groupNumber)
    {
        var sponsor = await GetByGroupNumberAsync(tenantId, groupNumber);
        return sponsor != null;
    }

    public async Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null)
    {
        var queryText = "SELECT VALUE COUNT(1) FROM c WHERE c.tenantId = @tenantId";
        var queryDef = new QueryDefinition(queryText).WithParameter("@tenantId", tenantId);

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@status", (int)status.Value);
        }

        var iterator = _container.GetItemQueryIterator<int>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return 0;
    }
}

/// <summary>
/// Repository interface for Sponsor entities
/// </summary>
public interface ISponsorRepository
{
    Task<Sponsor?> GetByIdAsync(string tenantId, string id);
    Task<Sponsor?> GetByGroupNumberAsync(string tenantId, string groupNumber);
    Task<(IEnumerable<Sponsor> Items, string? ContinuationToken, int TotalCount)> GetPagedAsync(
        string tenantId,
        SponsorStatus? status = null,
        bool activeOnly = false,
        LineOfBusiness? lineOfBusiness = null,
        int pageSize = 20,
        string? continuationToken = null);
    Task<Sponsor> CreateAsync(Sponsor sponsor);
    Task<Sponsor> UpdateAsync(Sponsor sponsor);
    Task DeleteAsync(string tenantId, string id);
    Task<bool> ExistsAsync(string tenantId, string groupNumber);
    Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null);
}
