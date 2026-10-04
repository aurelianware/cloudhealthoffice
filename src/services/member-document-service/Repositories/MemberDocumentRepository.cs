using System.Runtime.CompilerServices;
using MemberDocumentService.Models;
using Microsoft.Azure.Cosmos;

namespace MemberDocumentService.Repositories;

/// <summary>
/// Cosmos DB store. Saves are optimistic: a document read here remembers the
/// item's ETag, and <see cref="UpdateAsync"/> replaces the item only if it still
/// has that ETag (If-Match). A document changed by another request in between
/// is not overwritten; <see cref="MemberDocumentConcurrencyException"/> is thrown.
/// </summary>
public class MemberDocumentRepository : IMemberDocumentRepository
{
    private readonly Container _container;

    // The ETag of each document instance this repository read or wrote. Kept
    // beside the instance, not on the model, so it is never stored or returned.
    private readonly ConditionalWeakTable<MemberDocument, string> _etags = new();

    public MemberDocumentRepository(CosmosClient cosmosClient, string databaseName, IConfiguration configuration)
    {
        var containerName = configuration["CosmosDb:MemberDocumentsContainerName"] ?? "MemberDocuments";
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public async Task<MemberDocument> CreateAsync(MemberDocument document)
    {
        var response = await _container.CreateItemAsync(document, new PartitionKey(document.TenantId));
        return Remember(response);
    }

    public async Task<MemberDocument?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<MemberDocument>(id, new PartitionKey(tenantId));
            return Remember(response);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<MemberDocument>> ListByMemberIdAsync(string tenantId, string memberId, string? category = null)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId";
        if (!string.IsNullOrWhiteSpace(category))
        {
            queryText += " AND c.category = @category";
        }

        queryText += " ORDER BY c.uploadedDate DESC";

        var query = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        if (!string.IsNullOrWhiteSpace(category))
        {
            query.WithParameter("@category", category);
        }

        var results = new List<MemberDocument>();
        using var iterator = _container.GetItemQueryIterator<MemberDocument>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }

    public async Task<MemberDocument> UpdateAsync(MemberDocument document)
    {
        // A document this repository did not read cannot be saved blind.
        if (!_etags.TryGetValue(document, out var etag))
            throw new MemberDocumentConcurrencyException(document.Id);

        var expected = document.Version;
        document.Version = expected + 1;
        try
        {
            var response = await _container.ReplaceItemAsync(
                document, document.Id, new PartitionKey(document.TenantId),
                new ItemRequestOptions { IfMatchEtag = etag });
            return Remember(response);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            document.Version = expected;
            throw new MemberDocumentConcurrencyException(document.Id, ex);
        }
    }

    private MemberDocument Remember(ItemResponse<MemberDocument> response)
    {
        var document = response.Resource;
        if (document != null && !string.IsNullOrEmpty(response.ETag))
            _etags.AddOrUpdate(document, response.ETag);
        return document!;
    }
}
