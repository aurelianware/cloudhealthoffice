using TenantService.Models;

namespace TenantService.Services;

public interface ITenantUserService
{
    Task<TenantUser> CreateUserAsync(string tenantId, CreateTenantUserRequest request);
    Task<TenantUser> UpdateUserAsync(string tenantId, string userId, UpdateTenantUserRequest request);
    Task<TenantUser?> GetUserAsync(string userId);
    Task<TenantUser?> GetUserByEmailAsync(string tenantId, string email);
    Task<TenantUser?> GetUserByAzureAdObjectIdAsync(string azureAdObjectId);
    Task<IEnumerable<TenantUser>> GetUsersByTenantAsync(string tenantId);
    Task<IEnumerable<TenantUser>> GetUsersByRoleAsync(string tenantId, string roleName);
    Task<IEnumerable<TenantUser>> GetUsersByDepartmentAsync(string tenantId, string department);
    Task<IEnumerable<TenantUser>> GetDirectReportsAsync(string tenantId, string supervisorId);
    Task DeleteUserAsync(string tenantId, string userId);

    /// <summary>
    /// Clears the user's Entra link (oid and tid). Returns the link it removed.
    /// The user keeps its status: disable it too if the person must not get back
    /// in (someone from the tenant's own directory is linked again by email on
    /// their next sign-in).
    /// </summary>
    Task<(TenantUser User, string PreviousOid, string PreviousTid)> UnlinkUserAsync(string tenantId, string userId);
    Task<bool> HasPermissionAsync(string userId, string permission);
    Task RecordLoginAsync(string userId);
}
