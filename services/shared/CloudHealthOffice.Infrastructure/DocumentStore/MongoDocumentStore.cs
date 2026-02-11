using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CloudHealthOffice.Infrastructure.DocumentStore;

/// <summary>
/// MongoDB implementation of IDocumentStore.
/// Provides compatibility for DigitalOcean Managed MongoDB or MongoDB Atlas.
/// </summary>
public class MongoDocumentStore<T> : IDocumentStore<T> where T : class
{
    private readonly IMongoCollection<T> _collection;
    private readonly string _partitionKeyField;

    public MongoDocumentStore(
        IMongoClient mongoClient,
        string databaseName,
        string collectionName,
        string partitionKeyField = "tenantId")
    {
        _partitionKeyField = partitionKeyField;
        var database = mongoClient.GetDatabase(databaseName);
        _collection = database.GetCollection<T>(collectionName);
    }

    public async Task<T?> GetByIdAsync(string id, string partitionKey)
    {
        var filter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq("id", id),
            Builders<T>.Filter.Eq(_partitionKeyField, partitionKey)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<T>> QueryAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey)
    {
        // Convert Cosmos SQL to MongoDB filter
        var filter = BuildMongoFilter(query, parameters, partitionKey);
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<(IEnumerable<T> Items, string? ContinuationToken)> QueryWithPaginationAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey,
        int pageSize = 20,
        string? continuationToken = null)
    {
        var filter = BuildMongoFilter(query, parameters, partitionKey);
        
        // Parse continuation token (last document ID)
        var skip = 0;
        if (!string.IsNullOrEmpty(continuationToken))
        {
            skip = int.Parse(continuationToken);
        }

        var results = await _collection
            .Find(filter)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        // Generate continuation token if more results exist
        string? newContinuationToken = null;
        if (results.Count == pageSize)
        {
            var totalCount = await _collection.CountDocumentsAsync(filter);
            if (skip + pageSize < totalCount)
            {
                newContinuationToken = (skip + pageSize).ToString();
            }
        }

        return (results, newContinuationToken);
    }

    public async Task<T> UpsertAsync(T entity, string partitionKey)
    {
        // Extract ID from entity (assumes entity has "id" property)
        var idProperty = typeof(T).GetProperty("Id") ?? typeof(T).GetProperty("id");
        if (idProperty == null)
            throw new InvalidOperationException($"Entity type {typeof(T).Name} must have an 'Id' or 'id' property");

        var id = idProperty.GetValue(entity)?.ToString();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Entity ID cannot be null or empty");

        var filter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq("id", id),
            Builders<T>.Filter.Eq(_partitionKeyField, partitionKey)
        );

        var options = new ReplaceOptions { IsUpsert = true };
        await _collection.ReplaceOneAsync(filter, entity, options);
        return entity;
    }

    public async Task DeleteAsync(string id, string partitionKey)
    {
        var filter = Builders<T>.Filter.And(
            Builders<T>.Filter.Eq("id", id),
            Builders<T>.Filter.Eq(_partitionKeyField, partitionKey)
        );

        await _collection.DeleteOneAsync(filter);
    }

    public async Task<int> CountAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey)
    {
        var filter = BuildMongoFilter(query, parameters, partitionKey);
        var count = await _collection.CountDocumentsAsync(filter);
        return (int)count;
    }

    /// <summary>
    /// Convert Cosmos SQL query to MongoDB filter.
    /// This is a simplified implementation - for production, consider a SQL-to-MongoDB parser.
    /// </summary>
    private FilterDefinition<T> BuildMongoFilter(
        string cosmosQuery,
        Dictionary<string, object> parameters,
        string partitionKey)
    {
        var filters = new List<FilterDefinition<T>>
        {
            Builders<T>.Filter.Eq(_partitionKeyField, partitionKey)
        };

        // Parse simple WHERE clauses (extend this for complex queries)
        foreach (var param in parameters)
        {
            var fieldName = param.Key.Replace("@", "");
            
            // Skip tenantId since we already filtered by partition key
            if (fieldName == "tenantId")
                continue;

            filters.Add(Builders<T>.Filter.Eq(fieldName, param.Value));
        }

        return Builders<T>.Filter.And(filters);
    }
}
