using BenefitPlanService.Models;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Repositories;

/// <summary>
/// In-process cache decorator over the raw service-category mapping
/// repository (capability BP 5.6 — Service Category Mapping).
///
/// <para>
/// The resolver runs per-claim-line during adjudication, so a short
/// <see cref="IMemoryCache"/> wraps the per-<c>(tenantId, benefitPlanId)</c>
/// mapping list. Writes invalidate the cache for the affected scope.
/// </para>
///
/// <para>
/// Cache coherence across pods relies on the configured
/// <see cref="ServiceCategoryMappingOptions.CacheTtl"/>; there is no
/// distributed invalidation channel. Operator-authored changes propagate
/// across the cluster within one TTL window after the write.
/// </para>
///
/// <para>
/// The decorator implements all three seams (read, write, applied-record)
/// and forwards write operations to the inner storage backend, then
/// invalidates the cache entry that the read seam would have served. The
/// applied-record seam pass-throughs without caching since the seeder is
/// the only consumer and runs at most once per tenant per startup window.
/// </para>
/// </summary>
public sealed class CachingServiceCategoryMappingRepository :
    IServiceCategoryMappingRepository,
    IServiceCategoryMappingWriteRepository,
    ISystemDefaultsAppliedRecordRepository
{
    private readonly IServiceCategoryMappingRepository _readInner;
    private readonly IServiceCategoryMappingWriteRepository _writeInner;
    private readonly ISystemDefaultsAppliedRecordRepository _appliedInner;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<ServiceCategoryMappingOptions> _options;

    public CachingServiceCategoryMappingRepository(
        IServiceCategoryMappingRepository readInner,
        IServiceCategoryMappingWriteRepository writeInner,
        ISystemDefaultsAppliedRecordRepository appliedInner,
        IMemoryCache cache,
        IOptionsMonitor<ServiceCategoryMappingOptions> options)
    {
        _readInner = readInner;
        _writeInner = writeInner;
        _appliedInner = appliedInner;
        _cache = cache;
        _options = options;
    }

    // ── IServiceCategoryMappingRepository (cached read seam) ────────────────

    public async Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        var key = BuildCacheKey(tenantId, benefitPlanId);
        if (_cache.TryGetValue<IReadOnlyList<ServiceCategoryMapping>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var fresh = await _readInner.GetMappingsAsync(tenantId, benefitPlanId, ct);
        var ttl = _options.CurrentValue.CacheTtl;
        if (ttl > TimeSpan.Zero)
        {
            _cache.Set(key, fresh, ttl);
        }
        return fresh;
    }

    // ── IServiceCategoryMappingWriteRepository (write-through + invalidate)

    public Task<ServiceCategoryMapping?> GetByIdAsync(
        string tenantId, Guid id, CancellationToken ct = default)
        => _writeInner.GetByIdAsync(tenantId, id, ct);

    public async Task<ServiceCategoryMapping> CreateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        var created = await _writeInner.CreateAsync(mapping, ct);
        Invalidate(created.TenantId, created.BenefitPlanId);
        return created;
    }

    public async Task<ServiceCategoryMapping> UpdateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        // Look up the prior plan scope BEFORE the update so we can
        // invalidate both the old and new cache entries when the caller
        // re-scopes a mapping (rare, but supported).
        var existing = await _writeInner.GetByIdAsync(mapping.TenantId, mapping.Id, ct);
        var updated = await _writeInner.UpdateAsync(mapping, ct);
        Invalidate(updated.TenantId, updated.BenefitPlanId);
        if (existing is not null && existing.BenefitPlanId != updated.BenefitPlanId)
        {
            Invalidate(updated.TenantId, existing.BenefitPlanId);
        }
        return updated;
    }

    public async Task<bool> DeleteAsync(
        string tenantId, Guid id, CancellationToken ct = default)
    {
        // Resolve the mapping first so we know which cache scope to invalidate.
        var existing = await _writeInner.GetByIdAsync(tenantId, id, ct);
        var deleted = await _writeInner.DeleteAsync(tenantId, id, ct);
        if (deleted && existing is not null)
        {
            Invalidate(tenantId, existing.BenefitPlanId);
        }
        return deleted;
    }

    public Task<IReadOnlyList<ServiceCategoryMapping>> ListAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
        => _writeInner.ListAsync(tenantId, benefitPlanId, ct);

    // ── ISystemDefaultsAppliedRecordRepository (pass-through, no cache) ─────

    public Task<SystemDefaultsAppliedRecord?> GetAsync(string tenantId, CancellationToken ct = default)
        => _appliedInner.GetAsync(tenantId, ct);

    public Task UpsertAsync(SystemDefaultsAppliedRecord record, CancellationToken ct = default)
        => _appliedInner.UpsertAsync(record, ct);

    // ── helpers ─────────────────────────────────────────────────────────────

    private void Invalidate(string tenantId, Guid? benefitPlanId)
    {
        _cache.Remove(BuildCacheKey(tenantId, benefitPlanId));
    }

    internal static string BuildCacheKey(string tenantId, Guid? benefitPlanId)
        => $"svccatmap:{tenantId}:{benefitPlanId?.ToString() ?? "tenant-default"}";
}
