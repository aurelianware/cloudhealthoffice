using MongoDB.Driver;
using RfaiService.Models;

namespace RfaiService.Repositories;

public class RfaiRepositoryMongo : IRfaiRepository
{
    private readonly IMongoCollection<RfaiCase> _collection;
    private readonly ILogger<RfaiRepositoryMongo> _logger;

    public RfaiRepositoryMongo(IMongoDatabase database, ILogger<RfaiRepositoryMongo> logger)
    {
        _collection = database.GetCollection<RfaiCase>("RfaiCases");
        _logger = logger;

        // Compound indexes: (tenantId, authNumber) for auth-based lookups,
        // (tenantId, status) for open-case queries, (tenantId, createdAt) for history.
        var keys = Builders<RfaiCase>.IndexKeys;
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<RfaiCase>(keys.Ascending(r => r.TenantId).Ascending(r => r.AuthNumber)),
            new CreateIndexModel<RfaiCase>(keys.Ascending(r => r.TenantId).Ascending(r => r.Status)),
            new CreateIndexModel<RfaiCase>(keys.Ascending(r => r.TenantId).Descending(r => r.CreatedAt)),
        });
    }

    public async Task<RfaiCase?> GetByIdAsync(string tenantId, string id)
    {
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.Id, id),
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId));

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
    {
        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, tenantId),
            Builders<RfaiCase>.Filter.Eq(r => r.AuthNumber, authNumber));

        return await _collection
            .Find(filter)
            .SortByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
    {
        await _collection.InsertOneAsync(rfaiCase);
        return rfaiCase;
    }

    public async Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
    {
        rfaiCase.UpdatedAt = DateTime.UtcNow;

        var filter = Builders<RfaiCase>.Filter.And(
            Builders<RfaiCase>.Filter.Eq(r => r.Id, rfaiCase.Id),
            Builders<RfaiCase>.Filter.Eq(r => r.TenantId, rfaiCase.TenantId));

        await _collection.ReplaceOneAsync(filter, rfaiCase, new ReplaceOptions { IsUpsert = false });
        return rfaiCase;
    }
}
