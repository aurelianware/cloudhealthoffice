using TenantService.Models;

namespace TenantService.Services;

public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(string tenantId);
    Task<Tenant?> GetByTenantIdAsync(string tenantId);
    Task<IEnumerable<Tenant>> GetAllAsync(int pageSize = 100, string? continuationToken = null);
    Task<IEnumerable<Tenant>> GetByStatusAsync(string status);
    Task<Tenant> CreateAsync(Tenant tenant);
    Task<Tenant> UpdateAsync(Tenant tenant);
    Task DeleteAsync(string tenantId);
    Task<bool> ExistsAsync(string tenantId);
    Task<Tenant?> GetByApiKeyHashAsync(string keyHash);

    /// <summary>
    /// Sets a <c>pending</c> tenant to <c>active</c> in one conditional write.
    /// False when the tenant is missing or not pending at the moment of the
    /// write (for example, suspended meanwhile): nothing is changed then.
    /// </summary>
    Task<bool> TryActivatePendingAsync(string tenantId, string actor);
}
