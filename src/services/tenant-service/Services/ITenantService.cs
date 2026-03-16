using CloudHealthOffice.OperatingMode;
using TenantService.Models;

namespace TenantService.Services;

public interface ITenantService
{
    Task<Tenant> CreateTenantAsync(CreateTenantRequest request);
    Task<Tenant?> GetTenantAsync(string tenantId);
    Task<IEnumerable<Tenant>> GetAllTenantsAsync();
    Task<Tenant> UpdateTenantAsync(string tenantId, UpdateTenantRequest request);
    Task ActivateTenantAsync(string tenantId);
    Task SuspendTenantAsync(string tenantId);
    Task DeleteTenantAsync(string tenantId);
    
    Task<ApiKeyResponse> CreateApiKeyAsync(string tenantId, CreateApiKeyRequest request);
    Task<IEnumerable<ApiKey>> GetApiKeysAsync(string tenantId);
    Task RevokeApiKeyAsync(string tenantId, string keyId);
    Task<Tenant?> ValidateApiKeyAsync(string apiKey);
    
    Task UpdateUsageAsync(string tenantId, string metricName, int increment = 1);
    Task<UsageMetrics> GetUsageAsync(string tenantId);

    Task<OperatingModeConfiguration> GetOperatingModeAsync(string tenantId);
    Task<OperatingModeConfiguration> UpdateOperatingModeAsync(string tenantId, UpdateOperatingModeRequest request);
}
