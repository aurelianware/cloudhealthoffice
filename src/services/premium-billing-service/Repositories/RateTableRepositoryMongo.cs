using MongoDB.Driver;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

/// <summary>MongoDB: collection <c>RateTables</c>; <c>_id</c> is the tenant-scoped version id.</summary>
public sealed class RateTableRepositoryMongo : IRateTableRepository
{
    public const string CollectionName = "RateTables";

    private readonly IMongoCollection<RateTableRecord> _collection;
    private readonly IHttpContextAccessor _http;

    public RateTableRepositoryMongo(IMongoDatabase database, IHttpContextAccessor http)
    {
        _collection = database.GetCollection<RateTableRecord>(CollectionName);
        _http = http;
    }

    public async Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync()
    {
        var tenantId = RepositoryTenant.From(_http);
        return await _collection.Find(r => r.TenantId == tenantId).ToListAsync();
    }

    public async Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync(string rateTableId)
    {
        var tenantId = RepositoryTenant.From(_http);
        return await _collection.Find(r => r.TenantId == tenantId && r.RateTableId == rateTableId)
            .SortBy(r => r.Version).ToListAsync();
    }

    public async Task<RateTableRecord?> GetVersionAsync(string rateTableId, int version)
    {
        var tenantId = RepositoryTenant.From(_http);
        return await _collection.Find(r => r.TenantId == tenantId && r.RateTableId == rateTableId && r.Version == version)
            .FirstOrDefaultAsync();
    }

    public async Task<RateTableRecord> CreateVersionAsync(RateTableRecord record)
    {
        record.TenantId = RepositoryTenant.From(_http);
        record.Id = RateTableRecord.DocumentId(record.TenantId, record.RateTableId, record.Version);
        try
        {
            await _collection.InsertOneAsync(record);
            return record;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new RateTableVersionConflictException(record.RateTableId, record.Version);
        }
    }
}
