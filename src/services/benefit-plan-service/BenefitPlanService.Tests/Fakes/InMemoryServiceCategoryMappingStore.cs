using BenefitPlanService.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>
/// In-memory implementation of all three service-category mapping seams.
/// Used as the inner storage backend in tests so the
/// <see cref="CachingServiceCategoryMappingRepository"/> decorator and the
/// <c>SystemDefaultMappingSeeder</c> can be exercised without spinning up a
/// real Cosmos or Mongo client.
///
/// <para>
/// Tracks per-method call counts so cache-hit assertions can verify that
/// repeated reads from the decorator hit the inner store at most once per
/// (tenant, plan) within the cache TTL window.
/// </para>
/// </summary>
internal sealed class InMemoryServiceCategoryMappingStore :
    IServiceCategoryMappingRepository,
    IServiceCategoryMappingWriteRepository,
    ISystemDefaultsAppliedRecordRepository
{
    private readonly List<ServiceCategoryMapping> _mappings = new();
    private readonly Dictionary<string, SystemDefaultsAppliedRecord> _applied = new();

    public int GetMappingsCallCount { get; private set; }
    public int ListCallCount { get; private set; }
    public int CreateCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }

    public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        GetMappingsCallCount++;
        return Task.FromResult(Filter(tenantId, benefitPlanId));
    }

    public Task<ServiceCategoryMapping?> GetByIdAsync(
        string tenantId, Guid id, CancellationToken ct = default)
    {
        var match = _mappings.FirstOrDefault(m => m.TenantId == tenantId && m.Id == id);
        return Task.FromResult(match is null ? null : Clone(match));
    }

    public Task<ServiceCategoryMapping> CreateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        CreateCallCount++;
        if (mapping.Id == Guid.Empty) mapping.Id = Guid.NewGuid();
        if (mapping.CreatedAt == default) mapping.CreatedAt = DateTimeOffset.UtcNow;
        _mappings.Add(Clone(mapping));
        return Task.FromResult(Clone(mapping));
    }

    public Task<ServiceCategoryMapping> UpdateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        UpdateCallCount++;
        var existing = _mappings.FirstOrDefault(m => m.TenantId == mapping.TenantId && m.Id == mapping.Id)
            ?? throw new KeyNotFoundException($"Mapping {mapping.Id} not found.");
        _mappings.Remove(existing);
        _mappings.Add(Clone(mapping));
        return Task.FromResult(Clone(mapping));
    }

    public Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        DeleteCallCount++;
        var existing = _mappings.FirstOrDefault(m => m.TenantId == tenantId && m.Id == id);
        if (existing is null) return Task.FromResult(false);
        _mappings.Remove(existing);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ServiceCategoryMapping>> ListAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        ListCallCount++;
        return Task.FromResult(Filter(tenantId, benefitPlanId));
    }

    public Task<SystemDefaultsAppliedRecord?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        _applied.TryGetValue(tenantId, out var rec);
        return Task.FromResult(rec is null ? null : Clone(rec));
    }

    public Task UpsertAsync(SystemDefaultsAppliedRecord record, CancellationToken ct = default)
    {
        _applied[record.TenantId] = Clone(record);
        return Task.CompletedTask;
    }

    private IReadOnlyList<ServiceCategoryMapping> Filter(string tenantId, Guid? benefitPlanId)
    {
        // Match the production backends' newest-first ordering so resolver
        // tests see the same iteration order in-memory as in Cosmos / Mongo.
        return _mappings
            .Where(m => m.TenantId == tenantId && m.BenefitPlanId == benefitPlanId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(Clone)
            .ToList();
    }

    private static ServiceCategoryMapping Clone(ServiceCategoryMapping m) => new()
    {
        Id = m.Id,
        TenantId = m.TenantId,
        BenefitPlanId = m.BenefitPlanId,
        ServiceTypeCode = m.ServiceTypeCode,
        ServiceTypeDescription = m.ServiceTypeDescription,
        Rules = m.Rules.Select(r => new ProcedureCodeRule
        {
            Id = r.Id,
            Priority = r.Priority,
            CodeType = r.CodeType,
            CodePattern = r.CodePattern,
            CodeRangeEnd = r.CodeRangeEnd,
            PlaceOfServiceCode = r.PlaceOfServiceCode,
            RequiredModifier = r.RequiredModifier,
            RevenueCode = r.RevenueCode,
        }).ToList(),
        EffectiveStart = m.EffectiveStart,
        EffectiveEnd = m.EffectiveEnd,
        IsActive = m.IsActive,
        CreatedAt = m.CreatedAt,
    };

    private static SystemDefaultsAppliedRecord Clone(SystemDefaultsAppliedRecord r) => new()
    {
        Id = r.Id,
        TenantId = r.TenantId,
        AppliedSeedVersion = r.AppliedSeedVersion,
        AppliedAt = r.AppliedAt,
        MappingCount = r.MappingCount,
    };
}
