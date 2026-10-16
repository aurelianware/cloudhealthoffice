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
    /// <summary>
    /// Writes the user's profile fields (names, email, roles, department,
    /// supervisor, status, last login). Never the Entra link or invitation
    /// fields, which only token-service redemption/linking and
    /// <see cref="UnlinkAsync"/> change. Throws
    /// <see cref="UserChangedConcurrentlyException"/> if the stored user crossed
    /// the Invited boundary (an invitation was redeemed), or was written at all
    /// (its <see cref="TenantUser.Revision"/> moved on), since it was read, and
    /// when the user no longer exists.
    /// </summary>
    Task<TenantUser> UpdateAsync(TenantUser user);

    /// <summary>
    /// Sets only <see cref="TenantUser.LastLoginAt"/> (and bumps the revision).
    /// False when the user does not exist.
    /// </summary>
    Task<bool> RecordLoginAsync(string userId, DateTime at);

    /// <summary>Clears oid and tid. Null when the user is not in the tenant.</summary>
    Task<TenantUser?> UnlinkAsync(string tenantId, string userId, string actor);
    Task DeleteAsync(string id);
    Task<bool> ExistsAsync(string tenantId, string email);
}
