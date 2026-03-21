using MongoDB.Driver;
using TenantService.Models;

namespace TenantService.Services;

public class TenantRoleRepository : ITenantRoleRepository
{
    private readonly IMongoCollection<TenantRole> _collection;
    private readonly ILogger<TenantRoleRepository> _logger;

    public TenantRoleRepository(IMongoDatabase database, ILogger<TenantRoleRepository> logger)
    {
        _collection = database.GetCollection<TenantRole>("TenantRoles");
        _logger = logger;
    }

    public async Task<TenantRole?> GetByRoleNameAsync(string roleName)
    {
        return await _collection.Find(r => r.RoleName == roleName).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<TenantRole>> GetAllAsync()
    {
        return await _collection.Find(_ => true)
            .SortBy(r => r.RoleName)
            .ToListAsync();
    }

    public async Task<TenantRole> CreateAsync(TenantRole role)
    {
        await _collection.InsertOneAsync(role);
        _logger.LogInformation("Created role {RoleName}", SanitizeForLog(role.RoleName));
        return role;
    }

    public async Task<TenantRole> UpdateAsync(TenantRole role)
    {
        await _collection.ReplaceOneAsync(r => r.Id == role.Id, role);
        _logger.LogInformation("Updated role {RoleName}", SanitizeForLog(role.RoleName));
        return role;
    }

    public async Task DeleteAsync(string roleName)
    {
        await _collection.DeleteOneAsync(r => r.RoleName == roleName);
        _logger.LogInformation("Deleted role {RoleName}", SanitizeForLog(roleName));
    }

    public async Task SeedStandardRolesAsync()
    {
        foreach (var standardRole in StandardRoles.All)
        {
            var existing = await GetByRoleNameAsync(standardRole.RoleName);
            if (existing == null)
            {
                var role = new TenantRole
                {
                    RoleName = standardRole.RoleName,
                    Description = standardRole.Description,
                    Permissions = new List<string>(standardRole.Permissions),
                    IsBuiltIn = true
                };

                await CreateAsync(role);
                _logger.LogInformation("Seeded standard role {RoleName}", SanitizeForLog(role.RoleName));
            }
            else
            {
                existing.Description = standardRole.Description;
                existing.Permissions = new List<string>(standardRole.Permissions);
                existing.IsBuiltIn = true;
                await UpdateAsync(existing);
            }
        }
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
