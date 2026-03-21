using MongoDB.Driver;
using TenantService.Models;

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

        await _collection.InsertOneAsync(tenant);
        _logger.LogInformation("Created tenant {TenantId} ({TenantName})",
            SanitizeForLog(tenant.TenantId), SanitizeForLog(tenant.TenantName));

        return tenant;
    }

    public async Task<Tenant> UpdateAsync(Tenant tenant)
    {
        tenant.UpdatedAt = DateTime.UtcNow;

        await _collection.ReplaceOneAsync(t => t.Id == tenant.Id, tenant);
        _logger.LogInformation("Updated tenant {TenantId}", SanitizeForLog(tenant.TenantId));

        return tenant;
    }

    public async Task DeleteAsync(string tenantId)
    {
        var tenant = await GetByTenantIdAsync(tenantId);
        if (tenant != null)
        {
            await _collection.DeleteOneAsync(t => t.Id == tenant.Id);
            _logger.LogInformation("Deleted tenant {TenantId}", SanitizeForLog(tenantId));
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

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
