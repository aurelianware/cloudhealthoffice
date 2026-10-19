using MongoDB.Driver;
using TenantService.Models;
using CloudHealthOffice.Infrastructure.Logging;

namespace TenantService.Services;

public class TenantRepository : ITenantRepository
{
    private readonly IMongoCollection<Tenant> _collection;
    private readonly ILogger<TenantRepository> _logger;

    public TenantRepository(IMongoDatabase database, ILogger<TenantRepository> logger)
    {
        _collection = database.GetCollection<Tenant>("Tenants");
        _logger = logger;
    }

    public async Task<Tenant?> GetByIdAsync(string id)
    {
        return await _collection.Find(t => t.Id == id).FirstOrDefaultAsync();
    }

    public async Task<Tenant?> GetByTenantIdAsync(string tenantId)
    {
        return await _collection.Find(t => t.TenantId == tenantId).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync(int pageSize = 100, string? continuationToken = null)
    {
        return await _collection.Find(_ => true)
            .SortByDescending(t => t.CreatedAt)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<Tenant>> GetByStatusAsync(string status)
    {
        return await _collection.Find(t => t.Status == status).ToListAsync();
    }

    public async Task<Tenant> CreateAsync(Tenant tenant)
    {
        tenant.CreatedAt = DateTime.UtcNow;
        tenant.UpdatedAt = DateTime.UtcNow;
        tenant.Revision = 0;

        await _collection.InsertOneAsync(tenant);
        _logger.LogInformation("Created tenant {TenantId} ({TenantName})",
            LogSanitizer.Sanitize(tenant.TenantId), LogSanitizer.Sanitize(tenant.TenantName));

        return tenant;
    }

    public async Task<Tenant> UpdateAsync(Tenant tenant)
    {
        tenant.UpdatedAt = DateTime.UtcNow;

        // Conditional on the revision read, so a Stripe webhook and an admin
        // edit (or two admin edits) cannot silently overwrite each other; 0 also
        // matches tenants written before the revision existed.
        var f = Builders<Tenant>.Filter;
        var filter = f.Eq(t => t.Id, tenant.Id) & f.Eq(t => t.TenantId, tenant.TenantId);
        var expected = tenant.Revision;
        tenant.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), tenant);
        }
        catch
        {
            tenant.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            tenant.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new TenantChangedConcurrentlyException(tenant.TenantId, expected);
            // A missing tenant: nothing written, as before.
            return tenant;
        }

        _logger.LogInformation("Updated tenant {TenantId}", LogSanitizer.Sanitize(tenant.TenantId));

        return tenant;
    }

    /// <summary>The stored tenant still has the revision the write was based on (0 matches tenants written before the field existed).</summary>
    private static FilterDefinition<Tenant> RevisionIs(long expected)
    {
        var f = Builders<Tenant>.Filter;
        return expected == 0
            ? f.Or(f.Eq(t => t.Revision, 0L), f.Exists(t => t.Revision, false))
            : f.Eq(t => t.Revision, expected);
    }

    public async Task DeleteAsync(string tenantId)
    {
        var tenant = await GetByTenantIdAsync(tenantId);
        if (tenant != null)
        {
            await _collection.DeleteOneAsync(t => t.Id == tenant.Id);
            _logger.LogInformation("Deleted tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
        }
    }

    public async Task<bool> ExistsAsync(string tenantId)
    {
        return await _collection.Find(t => t.TenantId == tenantId).AnyAsync();
    }

    public async Task<Tenant?> GetByApiKeyHashAsync(string keyHash)
    {
        var filter = Builders<Tenant>.Filter.ElemMatch(
            t => t.ApiKeys,
            k => k.KeyHash == keyHash && k.IsActive);

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<bool> TryActivatePendingAsync(string tenantId, string actor)
    {
        var now = DateTime.UtcNow;
        var result = await _collection.UpdateOneAsync(
            t => t.TenantId == tenantId && t.Status == "pending",
            Builders<Tenant>.Update
                .Inc(t => t.Revision, 1L)
                .Set(t => t.Status, "active")
                .Set(t => t.ActivatedAt, now)
                .Set(t => t.UpdatedAt, now)
                .Set(t => t.UpdatedBy, actor));
        return result.ModifiedCount == 1;
    }
}
