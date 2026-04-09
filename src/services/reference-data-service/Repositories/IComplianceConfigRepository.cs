using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

/// <summary>
/// Persistence contract for tenant compliance configuration documents.
/// Implementations: Cosmos DB (production) and in-memory (fallback/test).
/// </summary>
public interface IComplianceConfigRepository
{
    Task<TenantComplianceConfig?> GetAsync(string tenantId);
    Task<TenantComplianceConfig> UpsertAsync(TenantComplianceConfig config);
}
