using MongoDB.Driver;
using TenantService.Models;

namespace TenantService.Services;

public class TenantUserRepository : ITenantUserRepository
{
    private readonly IMongoCollection<TenantUser> _collection;
    private readonly ILogger<TenantUserRepository> _logger;

    public TenantUserRepository(IMongoDatabase database, ILogger<TenantUserRepository> logger)
    {
        _collection = database.GetCollection<TenantUser>("TenantUsers");
        _logger = logger;

        // Ensure indexes for common queries
        _collection.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<TenantUser>(
                Builders<TenantUser>.IndexKeys
                    .Ascending(u => u.TenantId)
                    .Ascending(u => u.EmailNormalized)),
            new CreateIndexModel<TenantUser>(
                Builders<TenantUser>.IndexKeys
                    .Ascending(u => u.AzureAdObjectId))
        });
    }

    public async Task<TenantUser?> GetByIdAsync(string id)
    {
        return await _collection.Find(u => u.Id == id).FirstOrDefaultAsync();
    }

    public async Task<TenantUser?> GetByEmailAsync(string tenantId, string email)
    {
        var normalizedEmail = email.ToLowerInvariant();
        return await _collection.Find(u =>
            u.TenantId == tenantId && u.EmailNormalized == normalizedEmail).FirstOrDefaultAsync();
    }

    public async Task<TenantUser?> GetByAzureAdObjectIdAsync(string azureAdObjectId)
    {
        return await _collection.Find(u => u.AzureAdObjectId == azureAdObjectId).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<TenantUser>> GetByTenantIdAsync(string tenantId)
    {
        return await _collection.Find(u => u.TenantId == tenantId)
            .SortBy(u => u.DisplayName)
            .ToListAsync();
    }

    public async Task<IEnumerable<TenantUser>> GetByRoleAsync(string tenantId, string roleName)
    {
        var filter = Builders<TenantUser>.Filter.And(
            Builders<TenantUser>.Filter.Eq(u => u.TenantId, tenantId),
            Builders<TenantUser>.Filter.AnyEq(u => u.Roles, roleName));

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<IEnumerable<TenantUser>> GetByDepartmentAsync(string tenantId, string department)
    {
        return await _collection.Find(u => u.TenantId == tenantId && u.Department == department)
            .SortBy(u => u.DisplayName)
            .ToListAsync();
    }

    public async Task<IEnumerable<TenantUser>> GetBySupervisorIdAsync(string tenantId, string supervisorId)
    {
        return await _collection.Find(u => u.TenantId == tenantId && u.SupervisorId == supervisorId)
            .SortBy(u => u.DisplayName)
            .ToListAsync();
    }

    public async Task<TenantUser> CreateAsync(TenantUser user)
    {
        user.CreatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        user.EmailNormalized = user.Email.ToLowerInvariant();

        await _collection.InsertOneAsync(user);
        _logger.LogInformation("Created tenant user {Email} for tenant {TenantId}",
            SanitizeForLog(user.Email), SanitizeForLog(user.TenantId));

        return user;
    }

    public async Task<TenantUser> UpdateAsync(TenantUser user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        user.EmailNormalized = user.Email.ToLowerInvariant();

        await _collection.ReplaceOneAsync(u => u.Id == user.Id, user);
        _logger.LogInformation("Updated tenant user {UserId}", SanitizeForLog(user.Id));

        return user;
    }

    public async Task DeleteAsync(string id)
    {
        await _collection.DeleteOneAsync(u => u.Id == id);
        _logger.LogInformation("Deleted tenant user {UserId}", SanitizeForLog(id));
    }

    public async Task<bool> ExistsAsync(string tenantId, string email)
    {
        var normalizedEmail = email.ToLowerInvariant();
        return await _collection.Find(u =>
            u.TenantId == tenantId && u.EmailNormalized == normalizedEmail).AnyAsync();
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
