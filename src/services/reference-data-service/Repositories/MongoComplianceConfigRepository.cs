using MongoDB.Driver;
using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

/// <summary>
/// MongoDB-backed repository for <see cref="TenantComplianceConfig"/> documents.
/// Collection: <c>compliance-configs</c>.
/// The document <c>id</c> is set to the tenantId, matching the Cosmos implementation, so one
/// config is stored per tenant and lookups stay a single indexed read.
/// </summary>
public class MongoComplianceConfigRepository : IComplianceConfigRepository
{
    private const string CollectionName = "compliance-configs";

    private readonly IMongoCollection<TenantComplianceConfig> _collection;

    public MongoComplianceConfigRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<TenantComplianceConfig>(CollectionName);
    }

    public async Task<TenantComplianceConfig?> GetAsync(string tenantId)
    {
        var filter = Builders<TenantComplianceConfig>.Filter.Eq(x => x.TenantId, tenantId);
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<TenantComplianceConfig> UpsertAsync(TenantComplianceConfig config)
    {
        // Set document ID to tenantId for deterministic reads/upserts.
        // One config per tenant; callers should treat Id as managed by the repository.
        config.Id = config.TenantId;

        var filter = Builders<TenantComplianceConfig>.Filter.Eq(x => x.TenantId, config.TenantId);

        await _collection.ReplaceOneAsync(
            filter,
            config,
            new ReplaceOptions { IsUpsert = true });

        return config;
    }
}
