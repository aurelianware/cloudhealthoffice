using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// Storage interface for per-tenant enrollment configuration.
///
/// One document per tenant — document ID equals tenantId.
/// Reads are high-frequency (every gate evaluation); writes are rare
/// (tenant onboarding, admin config changes).
///
/// Implementations:
///   TenantEnrollmentConfigRepositoryCosmos — Cosmos DB
///   TenantEnrollmentConfigRepositoryMongo  — MongoDB
///   RedisTenantEnrollmentConfigRepository  — Redis cache layer
/// </summary>
public interface ITenantEnrollmentConfigRepository
{
    /// <summary>
    /// Load the enrollment configuration for a tenant.
    /// Returns null when no config document exists — gate treats this as Disabled.
    /// </summary>
    Task<TenantEnrollmentConfig?> GetAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Create or replace a tenant's enrollment configuration.</summary>
    Task UpsertAsync(TenantEnrollmentConfig config, CancellationToken ct = default);

    /// <summary>
    /// Delete a tenant's enrollment configuration.
    /// Idempotent — does not throw when the document does not exist.
    /// </summary>
    Task DeleteAsync(string tenantId, CancellationToken ct = default);

    /// <summary>List all tenant configs — used by the portal admin grid.</summary>
    Task<IReadOnlyList<TenantEnrollmentConfig>> ListAsync(CancellationToken ct = default);
}
