using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace CloudHealthOffice.Infrastructure.DocumentStore;

/// <summary>
/// Azure Cosmos DB implementation of IDocumentStore.
/// Wraps Cosmos SDK for multi-cloud abstraction.
/// </summary>
public class CosmosDocumentStore<T> : IDocumentStore<T> where T : class
{
    private readonly Container _container;
    private readonly string _partitionKeyPath;

    public CosmosDocumentStore(
        CosmosClient cosmosClient,
        string databaseName,
        string containerName,
        string partitionKeyPath = "/tenantId")
    {
        _partitionKeyPath = partitionKeyPath;
        var database = cosmosClient.GetDatabase(databaseName);
        _container = database.GetContainer(containerName);
    }

    public async Task<T?> GetByIdAsync(string id, string partitionKey)
    {
        try
        {
            var response = await _container.ReadItemAsync<T>(
                id,
                new PartitionKey(partitionKey));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<T>> QueryAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey)
    {
        var queryDefinition = new QueryDefinition(query);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter($"@{param.Key}", param.Value);
        }

        var iterator = _container.GetItemQueryIterator<T>(
            queryDefinition,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<(IEnumerable<T> Items, string? ContinuationToken)> QueryWithPaginationAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var queryDefinition = new QueryDefinition(query);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter($"@{param.Key}", param.Value);
        }

        var iterator = _container.GetItemQueryIterator<T>(
            queryDefinition,
            continuationToken: continuationToken,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey),
                MaxItemCount = pageSize
            });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return (response.ToList(), response.ContinuationToken);
        }

        return (Enumerable.Empty<T>(), null);
    }

    public async Task<T> UpsertAsync(T entity, string partitionKey)
    {
        var response = await _container.UpsertItemAsync(
            entity,
            new PartitionKey(partitionKey));
        return response.Resource;
    }

    public async Task DeleteAsync(string id, string partitionKey)
    {
        await _container.DeleteItemAsync<T>(
            id,
            new PartitionKey(partitionKey));
    }

    public async Task<int> CountAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey)
    {
        var countQuery = query.Replace("SELECT *", "SELECT VALUE COUNT(1)", StringComparison.OrdinalIgnoreCase);
        
        var queryDefinition = new QueryDefinition(countQuery);
        foreach (var param in parameters)
        {
            queryDefinition = queryDefinition.WithParameter($"@{param.Key}", param.Value);
        }

        var iterator = _container.GetItemQueryIterator<int>(
            queryDefinition,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return 0;
    }
}
