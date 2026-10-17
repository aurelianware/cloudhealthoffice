using MongoDB.Driver;
using TenantService.Models;
using CloudHealthOffice.Infrastructure.Logging;

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
        user.Revision = 0;

        await _collection.InsertOneAsync(user);
        _logger.LogInformation("Created tenant user {Email} for tenant {TenantId}",
            LogSanitizer.Sanitize(user.Email), LogSanitizer.Sanitize(user.TenantId));

        return user;
    }

    public async Task<TenantUser> UpdateAsync(TenantUser user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        user.EmailNormalized = user.Email.ToLowerInvariant();

        // Field updates, not a replace: a replace of a copy read earlier would
        // write back a stale Entra link or invitation state over a concurrent
        // redemption, link or unlink. The status guard refuses a write from a
        // copy read on the other side of the Invited boundary (for example, an
        // admin edit of an Invited user that has just been redeemed). The
        // revision guard refuses a write from a copy read before any other
        // write (a role removal or deactivation would otherwise be undone);
        // 0 also matches users written before the revision existed.
        var f = Builders<TenantUser>.Filter;
        var expected = user.Revision;
        var filter = f.And(
            f.Eq(u => u.Id, user.Id),
            user.Status == TenantUserStatus.Invited
                ? f.Eq(u => u.Status, TenantUserStatus.Invited)
                : f.Ne(u => u.Status, TenantUserStatus.Invited),
            expected == 0
                ? f.Or(f.Eq(u => u.Revision, 0L), f.Exists(u => u.Revision, false))
                : f.Eq(u => u.Revision, expected));
        var update = Builders<TenantUser>.Update
            .Set(u => u.Revision, expected + 1)
            .Set(u => u.Email, user.Email)
            .Set(u => u.EmailNormalized, user.EmailNormalized)
            .Set(u => u.DisplayName, user.DisplayName)
            .Set(u => u.FirstName, user.FirstName)
            .Set(u => u.LastName, user.LastName)
            .Set(u => u.Roles, user.Roles)
            .Set(u => u.Department, user.Department)
            .Set(u => u.SupervisorId, user.SupervisorId)
            .Set(u => u.Status, user.Status)
            .Set(u => u.LastLoginAt, user.LastLoginAt)
            .Set(u => u.UpdatedAt, user.UpdatedAt)
            .Set(u => u.UpdatedBy, user.UpdatedBy);

        var result = await _collection.UpdateOneAsync(filter, update);
        if (result.MatchedCount == 0)
            throw new UserChangedConcurrentlyException(user.Id);
        user.Revision = expected + 1;
        _logger.LogInformation("Updated tenant user {UserId}", LogSanitizer.Sanitize(user.Id));

        return user;
    }

    public async Task<bool> RecordLoginAsync(string userId, DateTime at)
    {
        // Only LastLoginAt: a login never writes back roles or status it read.
        var update = Builders<TenantUser>.Update
            .Set(u => u.LastLoginAt, at)
            .Inc(u => u.Revision, 1L);
        var result = await _collection.UpdateOneAsync(u => u.Id == userId, update);
        return result.MatchedCount > 0;
    }

    public async Task<TenantUser?> UnlinkAsync(string tenantId, string userId, string actor)
    {
        var update = Builders<TenantUser>.Update
            .Inc(u => u.Revision, 1L)
            .Set(u => u.AzureAdObjectId, string.Empty)
            .Set(u => u.AzureAdTenantId, string.Empty)
            .Set(u => u.UpdatedAt, DateTime.UtcNow)
            .Set(u => u.UpdatedBy, actor);
        return await _collection.FindOneAndUpdateAsync<TenantUser>(
            u => u.Id == userId && u.TenantId == tenantId, update,
            new FindOneAndUpdateOptions<TenantUser> { ReturnDocument = ReturnDocument.After });
    }

    public async Task DeleteAsync(string id)
    {
        await _collection.DeleteOneAsync(u => u.Id == id);
        _logger.LogInformation("Deleted tenant user {UserId}", LogSanitizer.Sanitize(id));
    }

    public async Task<bool> ExistsAsync(string tenantId, string email)
    {
        var normalizedEmail = email.ToLowerInvariant();
        return await _collection.Find(u =>
            u.TenantId == tenantId && u.EmailNormalized == normalizedEmail).AnyAsync();
    }
}
