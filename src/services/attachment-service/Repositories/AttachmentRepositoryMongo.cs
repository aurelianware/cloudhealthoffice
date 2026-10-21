using CloudHealthOffice.Infrastructure.Data;
using AttachmentService.Models;
using AttachmentService.Services;
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

        // The matcher's work list (status, oldest first); created once per process.
        if (Interlocked.Exchange(ref _indexEnsured, 1) == 0)
        {
            _collection.Indexes.CreateOne(new CreateIndexModel<Attachment>(
                Builders<Attachment>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.CreatedDate),
                new CreateIndexOptions { Name = "status_created" }));
        }
    }

    private static int _indexEnsured;

    public async Task<IReadOnlyList<Attachment>> GetUnmatchedAsync(string tenantId, int limit = 200)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Attachment>.Filter.Eq(x => x.Status, "Unmatched"));
        return await _collection.Find(filter).SortBy(x => x.CreatedDate).Limit(limit).ToListAsync();
    }

    [CrossTenantQuery("UnmatchedAttachmentMatcher (background): finds held 275s across tenants, then matches and links each under its own TenantId.")]
    public async Task<IReadOnlyList<Attachment>> ListUnmatchedAsync(int limit)
    {
        var filter = Builders<Attachment>.Filter.Eq(x => x.Status, "Unmatched");
        return await _collection.Find(filter).SortBy(x => x.CreatedDate).Limit(limit).ToListAsync();
    }

    public async Task<Attachment> CreateAsync(Attachment attachment)
    {
        attachment.CreatedDate = DateTime.UtcNow;
        attachment.Revision = 0;
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

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Attachment> RevisionIs(long expected)
    {
        var b = Builders<Attachment>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<Attachment> UpdateAsync(Attachment attachment)
    {
        var filter = Builders<Attachment>.Filter.And(
            Builders<Attachment>.Filter.Eq(x => x.Id, attachment.Id),
            Builders<Attachment>.Filter.Eq(x => x.TenantId, attachment.TenantId)
        );

        // Conditional on the revision the caller read: a concurrent write in
        // between makes this one fail instead of being silently overwritten.
        var expected = attachment.Revision;
        attachment.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), attachment);
        }
        catch
        {
            attachment.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            attachment.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new AttachmentConcurrencyException(attachment.Id, expected);

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
