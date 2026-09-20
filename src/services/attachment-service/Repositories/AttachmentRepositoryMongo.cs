using AttachmentService.Models;
using MongoDB.Driver;

namespace AttachmentService.Repositories;

public class AttachmentRepositoryMongo : IAttachmentRepository
{
    private readonly IMongoCollection<Attachment> _collection;
    private readonly ILogger<AttachmentRepositoryMongo> _logger;

    public AttachmentRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<AttachmentRepositoryMongo> logger)
    {
        var collectionName = configuration["MongoDb:AttachmentsCollectionName"]
            ?? configuration["CosmosDb:AttachmentsContainerName"]
            ?? "Attachments";
        _collection = database.GetCollection<Attachment>(collectionName);
        _logger = logger;
    }

    public async Task<Attachment> CreateAsync(Attachment attachment)
    {
        attachment.CreatedDate = DateTime.UtcNow;
        await _collection.InsertOneAsync(attachment);
        return attachment;
    }

    public async Task<Attachment?> GetByIdAsync(string id, string tenantId)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.Id, id),
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Attachment>> GetByClaimIdAsync(string claimId, string tenantId)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Attachment>.Filter.Eq(x => x.ClaimId, claimId)
        );
        return await FindDescendingBySubmittedDateAsync(filter);
    }

    public async Task<IEnumerable<Attachment>> GetByAuthorizationIdAsync(string authorizationId, string tenantId)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Attachment>.Filter.Eq(x => x.AuthorizationId, authorizationId)
        );
        return await FindDescendingBySubmittedDateAsync(filter);
    }

    public async Task<IEnumerable<Attachment>> GetByAppealIdAsync(string appealId, string tenantId)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Attachment>.Filter.Eq(x => x.AppealId, appealId)
        );
        return await FindDescendingBySubmittedDateAsync(filter);
    }

    public async Task<Attachment?> GetByRFAIReferenceAsync(string rfaiReference, string tenantId)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Attachment>.Filter.Eq(x => x.RFAIReference, rfaiReference)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Attachment> UpdateAsync(Attachment attachment)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.Id, attachment.Id),
            Builders<Attachment>.Filter.Eq(x => x.TenantId, attachment.TenantId)
        );

        var result = await _collection.ReplaceOneAsync(filter, attachment);

        if (result.MatchedCount == 0)
        {
            throw new InvalidOperationException(
                $"Attachment '{attachment.Id}' was not found for tenant '{attachment.TenantId}'.");
        }

        return attachment;
    }

    public async Task DeleteAsync(string id, string tenantId)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.Id, id),
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId)
        );

        var result = await _collection.DeleteOneAsync(filter);

        if (result.DeletedCount == 0)
        {
            _logger.LogWarning(
                "Delete requested for attachment {AttachmentId} in tenant {TenantId}, but no document matched",
                id, tenantId);
        }
    }

    private async Task<List<Attachment>> FindDescendingBySubmittedDateAsync(FilterDefinition<Attachment> filter)
    {
        return await _collection
            .Find(filter)
            .SortByDescending(x => x.SubmittedDate)
            .ToListAsync();
    }
}
