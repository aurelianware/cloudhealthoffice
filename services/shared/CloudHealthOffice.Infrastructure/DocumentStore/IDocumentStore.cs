using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudHealthOffice.Infrastructure.DocumentStore;

/// <summary>
/// Cloud-agnostic document store abstraction.
/// Supports both Azure Cosmos DB and MongoDB implementations.
/// </summary>
public interface IDocumentStore<T> where T : class
{
    /// <summary>
    /// Get a document by ID and partition key.
    /// </summary>
    Task<T?> GetByIdAsync(string id, string partitionKey);

    /// <summary>
    /// Query documents with custom query and partition key.
    /// </summary>
    Task<IEnumerable<T>> QueryAsync(string query, Dictionary<string, object> parameters, string partitionKey);

    /// <summary>
    /// Query documents with pagination support.
    /// </summary>
    Task<(IEnumerable<T> Items, string? ContinuationToken)> QueryWithPaginationAsync(
        string query,
        Dictionary<string, object> parameters,
        string partitionKey,
        int pageSize = 20,
        string? continuationToken = null);

    /// <summary>
    /// Insert or update a document.
    /// </summary>
    Task<T> UpsertAsync(T entity, string partitionKey);

    /// <summary>
    /// Delete a document by ID and partition key.
    /// </summary>
    Task DeleteAsync(string id, string partitionKey);

    /// <summary>
    /// Get count of documents matching query.
    /// </summary>
    Task<int> CountAsync(string query, Dictionary<string, object> parameters, string partitionKey);
}
