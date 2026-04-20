using MemberDocumentService.Models;
using Microsoft.Azure.Cosmos;

namespace MemberDocumentService.Repositories;

public class MemberDocumentRepository : IMemberDocumentRepository
{
    private readonly Container _container;

    public MemberDocumentRepository(CosmosClient cosmosClient, string databaseName, IConfiguration configuration)
    {
        var containerName = configuration["CosmosDb:MemberDocumentsContainerName"] ?? "MemberDocuments";
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public async Task<MemberDocument> CreateAsync(MemberDocument document)
    {
        var response = await _container.CreateItemAsync(document, new PartitionKey(document.TenantId));
        return response.Resource;
    }

    public async Task<MemberDocument?> GetByIdAsync(string tenantId, string id)
    {
        try
        {
            var response = await _container.ReadItemAsync<MemberDocument>(id, new PartitionKey(tenantId));
            return response.Resource;
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
        var response = await _container.ReplaceItemAsync(document, document.Id, new PartitionKey(document.TenantId));
        return response.Resource;
    }
}
