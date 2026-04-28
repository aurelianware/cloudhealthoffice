using CloudHealthOffice.BenefitEngine.Domain;

namespace BenefitPlanService.Repositories;

/// <summary>
/// Write seam for service-category mappings (capability BP 5.6 — Service
/// Category Mapping). The read seam — <c>IServiceCategoryMappingRepository</c>
/// in the BenefitEngine class library — is consumed by
/// <c>ServiceCategoryResolver</c> on the adjudication hot path. The write seam
/// is consumed by the admin controller and by the
/// <c>SystemDefaultMappingSeeder</c> hosted service.
///
/// <para>
/// Mappings are <c>last-write-wins</c>: there is no version chain (unlike
/// <c>BenefitPlan</c>). The admin actor + correlation-id captured by
/// structured request logging provides the operational audit trail.
/// </para>
///
/// <para>
/// Both backends (Cosmos / Mongo) implement both read and write seams via
/// the same physical class. Writes invalidate the in-process cache the read
/// path uses; cross-pod cache coherence relies on the configured
/// <see cref="Models.ServiceCategoryMappingOptions.CacheTtl"/> rather than a
/// distributed invalidation channel.
/// </para>
/// </summary>
public interface IServiceCategoryMappingWriteRepository
{
    /// <summary>
    /// Fetch a single mapping by document id. Returns null when not found
    /// or when the mapping belongs to a different tenant (tenant isolation
    /// is enforced at the repository boundary, not the controller).
    /// </summary>
    Task<ServiceCategoryMapping?> GetByIdAsync(
        string tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Persist a new mapping. The repository assigns a fresh <c>Id</c> when
    /// the supplied value is <c>Guid.Empty</c>; otherwise it preserves the
    /// caller-supplied value. Returns the persisted entity (with id
    /// populated).
    /// </summary>
    Task<ServiceCategoryMapping> CreateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default);

    /// <summary>
    /// Replace an existing mapping. Throws
    /// <see cref="KeyNotFoundException"/> when the row doesn't exist or
    /// belongs to a different tenant. The supplied entity's
    /// <c>TenantId</c> and <c>Id</c> identify the row; all other fields
    /// are replaced wholesale.
    /// </summary>
    Task<ServiceCategoryMapping> UpdateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete a mapping. Returns <c>true</c> when a row was removed;
    /// <c>false</c> when no matching row existed (tenant isolation
    /// enforced — a mismatched tenant id is treated as not-found).
    /// </summary>
    Task<bool> DeleteAsync(
        string tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Read seam re-exposed on the write interface for the seeder's
    /// idempotency check (it needs to know whether system defaults were
    /// already applied for a tenant) and for the controller's
    /// list/by-id reads. Cache-bypass: this overload always hits the
    /// underlying store, so callers that need cache semantics should use
    /// the read seam (<c>IServiceCategoryMappingRepository</c>).
    /// </summary>
    Task<IReadOnlyList<ServiceCategoryMapping>> ListAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default);
}

/// <summary>
/// Per-tenant record of which seed-bundle version has been applied. The
/// seeder skips a tenant when the recorded version matches the bundle
/// version on disk; admins re-trigger by bumping the file's version field
/// and forcing the seeder to run (see
/// <c>docs/architecture/service-category-mapping.md</c>).
/// </summary>
public sealed class SystemDefaultsAppliedRecord
{
    public string Id { get; set; } = "system-defaults-applied";
    public string TenantId { get; set; } = default!;
    public int AppliedSeedVersion { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public int MappingCount { get; set; }
}

/// <summary>
/// Records the per-tenant <c>SystemDefaultsApplied</c> idempotency
/// document. Lives alongside the mappings collection (same store, distinct
/// document id) so the seeder doesn't need a second collection.
/// </summary>
public interface ISystemDefaultsAppliedRecordRepository
{
    Task<SystemDefaultsAppliedRecord?> GetAsync(string tenantId, CancellationToken ct = default);

    Task UpsertAsync(SystemDefaultsAppliedRecord record, CancellationToken ct = default);
}
