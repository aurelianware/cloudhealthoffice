using Microsoft.Azure.Cosmos;
using CoverageService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CoverageService.Repositories;

/// <summary>
/// Cosmos DB repository for Coverage entities.
/// Uses TenantId as partition key for multi-tenant isolation.
/// </summary>
public class CoverageRepository : ICoverageRepository
{
    private readonly Container _container;
    private const string ContainerName = "Coverage";
    private const string PartitionKeyPath = "/tenantId";

    public CoverageRepository(CosmosClient cosmosClient, string databaseName)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        _container = database.GetContainer(ContainerName);
    }

    public async Task<Coverage?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<Coverage>(
                id,
                new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? insuranceLineCode = null)
    {
        var queryText = @"
            SELECT * FROM c 
            WHERE c.tenantId = @tenantId 
            AND c.memberId = @memberId
            AND c.status = @activeStatus
            AND c.effectiveDate <= @serviceDate
            AND (NOT IS_DEFINED(c.terminationDate) OR c.terminationDate >= @serviceDate)";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId)
            .WithParameter("@activeStatus", (int)CoverageStatus.Active)
            .WithParameter("@serviceDate", serviceDate.Date);

        if (!string.IsNullOrEmpty(insuranceLineCode))
        {
            queryText += " AND c.insuranceLineCode = @insuranceLineCode";
            queryDef.WithParameter("@insuranceLineCode", insuranceLineCode);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<List<Coverage>> GetCoverageHistoryAsync(
        string tenantId,
        string memberId,
        bool includeTerminated = true)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId";
        
        if (!includeTerminated)
        {
            queryText += " AND c.status != @terminatedStatus";
        }

        queryText += " ORDER BY c.effectiveDate DESC";

        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        if (!includeTerminated)
        {
            queryDef.WithParameter("@terminatedStatus", (int)CoverageStatus.Terminated);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId";
        var parameters = new List<(string Name, object Value)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(memberId))
        {
            queryText += " AND c.memberId = @memberId";
            parameters.Add(("@memberId", memberId));
        }

        if (!string.IsNullOrEmpty(groupNumber))
        {
            queryText += " AND c.groupNumber = @groupNumber";
            parameters.Add(("@groupNumber", groupNumber));
        }

        if (!string.IsNullOrEmpty(planId))
        {
            queryText += " AND c.planId = @planId";
            parameters.Add(("@planId", planId));
        }

        if (activeOnly)
        {
            queryText += " AND c.status = @activeStatus";
            parameters.Add(("@activeStatus", (int)CoverageStatus.Active));
        }

        var queryDef = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = pageSize
            });

        var results = new List<Coverage>();
        string? newContinuationToken = null;

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
            newContinuationToken = response.ContinuationToken;
        }

        return (results, newContinuationToken);
    }

    public async Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        var iterator = _container.GetItemQueryIterator<Coverage>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<List<Coverage>> GetByPcpNpiAsync(
        string tenantId,
        string pcpNpi,
        CoverageStatus? status = null,
        LineOfBusiness? lineOfBusiness = null)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.pcpNpi = @pcpNpi";
        var parameters = new List<(string Name, object Value)>
        {
            ("@tenantId", tenantId),
            ("@pcpNpi", pcpNpi)
        };

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", (int)status.Value));
        }

        if (lineOfBusiness.HasValue)
        {
            queryText += " AND c.lineOfBusiness = @lineOfBusiness";
            parameters.Add(("@lineOfBusiness", (int)lineOfBusiness.Value));
        }

        var queryDef = new QueryDefinition(queryText);
        foreach (var (name, value) in parameters)
        {
            queryDef.WithParameter(name, value);
        }

        var iterator = _container.GetItemQueryIterator<Coverage>(
            queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId)
            });

        var results = new List<Coverage>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null)
    {
        var queryText = "SELECT VALUE COUNT(1) FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber";
        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            queryDef = new QueryDefinition(queryText)
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@groupNumber", groupNumber)
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

    public async Task<Coverage> CreateAsync(Coverage coverage)
    {
        coverage.CreatedDate = DateTime.UtcNow;
        coverage.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(
            coverage,
            new PartitionKey(coverage.TenantId));

        return response.Resource;
    }

    public async Task<Coverage> UpdateAsync(Coverage coverage)
    {
        coverage.LastUpdatedDate = DateTime.UtcNow;

        var response = await _container.ReplaceItemAsync(
            coverage,
            coverage.Id,
            new PartitionKey(coverage.TenantId));

        return response.Resource;
    }

    public async Task DeleteAsync(string tenantId, string id)
    {
        await _container.DeleteItemAsync<Coverage>(
            id,
            new PartitionKey(tenantId));
    }
}

/// <summary>
/// Repository interface for Coverage entities
/// </summary>
public interface ICoverageRepository
{
    Task<Coverage?> GetByIdAsync(string tenantId, string id);
    Task<List<Coverage>> GetActiveCoverageByMemberIdAsync(string tenantId, string memberId, DateTime serviceDate, string? insuranceLineCode = null);
    Task<List<Coverage>> GetCoverageHistoryAsync(string tenantId, string memberId, bool includeTerminated = true);
    Task<(IEnumerable<Coverage> Items, string? ContinuationToken)> SearchAsync(
        string tenantId,
        string? memberId = null,
        string? groupNumber = null,
        string? planId = null,
        bool activeOnly = false,
        int pageSize = 20,
        string? continuationToken = null);
    Task<List<Coverage>> GetByGroupNumberAsync(string tenantId, string groupNumber);
    Task<List<Coverage>> GetByPcpNpiAsync(string tenantId, string pcpNpi, CoverageStatus? status = null, LineOfBusiness? lineOfBusiness = null);
    Task<int> GetCountByGroupAsync(string tenantId, string groupNumber, CoverageStatus? status = null);
    Task<Coverage> CreateAsync(Coverage coverage);
    Task<Coverage> UpdateAsync(Coverage coverage);
    Task DeleteAsync(string tenantId, string id);
}
