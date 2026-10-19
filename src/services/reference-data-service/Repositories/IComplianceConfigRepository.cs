using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

/// <summary>
/// Persistence contract for tenant compliance configuration documents.
/// Implementations: MongoDB (production) and in-memory (Development / Testing).
/// </summary>
public interface IComplianceConfigRepository
{
    Task<TenantComplianceConfig?> GetAsync(string tenantId);
    Task<TenantComplianceConfig> UpsertAsync(TenantComplianceConfig config);
}
