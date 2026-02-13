using Microsoft.Azure.Cosmos;
using AttachmentService.Models;

namespace AttachmentService.Repositories;

public class AttachmentRepository : IAttachmentRepository
{
    private readonly Container _container;

    public AttachmentRepository(CosmosClient cosmosClient, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        var containerName = configuration["CosmosDb:AttachmentsContainerName"] ?? "Attachments";
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public async Task<Attachment> CreateAsync(Attachment attachment)
    {
        attachment.CreatedDate = DateTime.UtcNow;
        var response = await _container.CreateItemAsync(attachment, new PartitionKey(attachment.TenantId));
        return response.Resource;
    }

    public async Task<Attachment?> GetByIdAsync(string id, string tenantId)
    {
        try
        {
            var response = await _container.ReadItemAsync<Attachment>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<Attachment>> GetByClaimIdAsync(string claimId, string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.claimId = @claimId ORDER BY c.submittedDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@claimId", claimId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<Attachment>> GetByAuthorizationIdAsync(string authorizationId, string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.authorizationId = @authorizationId ORDER BY c.submittedDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@authorizationId", authorizationId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<Attachment>> GetByAppealIdAsync(string appealId, string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.appealId = @appealId ORDER BY c.submittedDate DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@appealId", appealId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<Attachment?> GetByRFAIReferenceAsync(string rfaiReference, string tenantId)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.rfaiReference = @rfaiReference")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@rfaiReference", rfaiReference);

        var results = await ExecuteQueryAsync(query);
        return results.FirstOrDefault();
    }

    public async Task<Attachment> UpdateAsync(Attachment attachment)
    {
        var response = await _container.ReplaceItemAsync(
            attachment, 
            attachment.Id, 
            new PartitionKey(attachment.TenantId));
        return response.Resource;
    }

    public async Task DeleteAsync(string id, string tenantId)
    {
        await _container.DeleteItemAsync<Attachment>(id, new PartitionKey(tenantId));
    }

    private async Task<List<Attachment>> ExecuteQueryAsync(QueryDefinition query)
    {
        var results = new List<Attachment>();
        using var iterator = _container.GetItemQueryIterator<Attachment>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        return results;
    }
}
