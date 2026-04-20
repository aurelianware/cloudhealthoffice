using MemberDocumentService.Models;
using MongoDB.Driver;

namespace MemberDocumentService.Repositories;

public class MemberDocumentRepositoryMongo : IMemberDocumentRepository
{
    private readonly IMongoCollection<MemberDocument> _collection;

    public MemberDocumentRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<MemberDocument>("MemberDocuments");

        _collection.Indexes.CreateOne(new CreateIndexModel<MemberDocument>(
            Builders<MemberDocument>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.MemberId)));

        _collection.Indexes.CreateOne(new CreateIndexModel<MemberDocument>(
            Builders<MemberDocument>.IndexKeys.Ascending(x => x.TenantId).Ascending(x => x.MemberId).Ascending(x => x.Category)));
    }

    public async Task<MemberDocument> CreateAsync(MemberDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Id))
        {
            document.Id = Guid.NewGuid().ToString();
        }

        await _collection.InsertOneAsync(document);
        return document;
    }

    public async Task<MemberDocument?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<MemberDocument>.Filter.Eq(x => x.Id, id)
                     & Builders<MemberDocument>.Filter.Eq(x => x.TenantId, tenantId);

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<MemberDocument>> ListByMemberIdAsync(string tenantId, string memberId, string? category = null)
    {
        var filter = Builders<MemberDocument>.Filter.Eq(x => x.TenantId, tenantId)
                     & Builders<MemberDocument>.Filter.Eq(x => x.MemberId, memberId);

        if (!string.IsNullOrWhiteSpace(category))
        {
            filter &= Builders<MemberDocument>.Filter.Eq(x => x.Category, category);
        }

        return await _collection.Find(filter)
            .SortByDescending(x => x.UploadedDate)
            .ToListAsync();
    }

    public async Task<MemberDocument> UpdateAsync(MemberDocument document)
    {
        var filter = Builders<MemberDocument>.Filter.Eq(x => x.Id, document.Id)
                     & Builders<MemberDocument>.Filter.Eq(x => x.TenantId, document.TenantId);

        await _collection.ReplaceOneAsync(filter, document);
        return document;
    }
}
