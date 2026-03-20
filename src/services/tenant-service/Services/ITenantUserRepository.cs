using TenantService.Models;

namespace TenantService.Services;

public interface ITenantUserRepository
{
    Task<TenantUser?> GetByIdAsync(string id);
    Task<TenantUser?> GetByEmailAsync(string tenantId, string email);
    Task<TenantUser?> GetByAzureAdObjectIdAsync(string azureAdObjectId);
    Task<IEnumerable<TenantUser>> GetByTenantIdAsync(string tenantId);
    Task<IEnumerable<TenantUser>> GetByRoleAsync(string tenantId, string roleName);
    Task<IEnumerable<TenantUser>> GetByDepartmentAsync(string tenantId, string department);
    Task<IEnumerable<TenantUser>> GetBySupervisorIdAsync(string tenantId, string supervisorId);
    Task<TenantUser> CreateAsync(TenantUser user);
    Task<TenantUser> UpdateAsync(TenantUser user);
    Task DeleteAsync(string id);
    Task<bool> ExistsAsync(string tenantId, string email);
}
