using TenantService.Models;

namespace TenantService.Services;

public interface ITenantRoleRepository
{
    Task<TenantRole?> GetByRoleNameAsync(string roleName);
    Task<IEnumerable<TenantRole>> GetAllAsync();
    Task<TenantRole> CreateAsync(TenantRole role);
    Task<TenantRole> UpdateAsync(TenantRole role);
    Task DeleteAsync(string roleName);
    Task SeedStandardRolesAsync();
}
