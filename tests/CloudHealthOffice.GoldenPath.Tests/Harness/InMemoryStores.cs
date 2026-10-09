using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;

namespace CloudHealthOffice.GoldenPath.Tests.Harness;

/// <summary>Fee schedules as benefit-plan-service stores them, held in memory.</summary>
internal sealed class InMemoryFeeScheduleRepository : IFeeScheduleRepository
{
    private readonly List<FeeSchedule> _schedules;

    public InMemoryFeeScheduleRepository(IEnumerable<FeeSchedule> schedules) => _schedules = schedules.ToList();

    public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult(_schedules.FirstOrDefault(s => s.TenantId == tenantId && s.Id == id));

    // No plan-default schedule: every scenario prices through a provider contract.
    public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
        => Task.FromResult<FeeSchedule?>(null);

    public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
        => Task.FromResult(_schedules.FirstOrDefault(s => s.Id == feeScheduleId)?.Lines
            .FirstOrDefault(l => l.ProcedureCode == procedureCode && l.Modifier == modifier));

    public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FeeSchedule>>(_schedules.Where(s => s.TenantId == tenantId).ToList());
}

/// <summary>Provider contracts, matched by tenant + NPI + plan + effective window.</summary>
internal sealed class InMemoryProviderContractRepository : IProviderContractRepository
{
    private readonly List<ProviderContract> _contracts;

    public InMemoryProviderContractRepository(IEnumerable<ProviderContract> contracts) => _contracts = contracts.ToList();

    public Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
        => Task.FromResult(_contracts.FirstOrDefault(c =>
            c.TenantId == tenantId && c.ProviderNpi == providerNpi && c.PlanId == planId
            && c.EffectiveDate <= serviceDate && (c.TermDate is null || c.TermDate >= serviceDate)));

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(string tenantId, string providerNpi, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProviderContract>>(_contracts.Where(c => c.ProviderNpi == providerNpi).ToList());
}

/// <summary>Tenant-level service category mappings (procedure / revenue code → plan benefit category).</summary>
internal sealed class InMemoryServiceCategoryMappingRepository : IServiceCategoryMappingRepository
{
    private readonly List<ServiceCategoryMapping> _mappings;

    public InMemoryServiceCategoryMappingRepository(IEnumerable<ServiceCategoryMapping> mappings) => _mappings = mappings.ToList();

    public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ServiceCategoryMapping>>(
            _mappings.Where(m => m.TenantId == tenantId && m.BenefitPlanId == benefitPlanId).ToList());
}

/// <summary>
/// Accumulators that remember what the engine applied. Seeded with the
/// member's prior activity this plan year (what earlier claims consumed).
/// </summary>
internal sealed class InMemoryAccumulatorService : IAccumulatorService
{
    private readonly Dictionary<(AccumulatorType, AccumulatorScope, NetworkTier), decimal> _accumulated = new();

    public List<AccumulatorUpdate> Applied { get; } = new();

    public void Seed(AccumulatorType type, AccumulatorScope scope, decimal accumulated, NetworkTier tier = NetworkTier.InNetwork)
        => _accumulated[(type, scope, tier)] = accumulated;

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccumulatorSnapshot>>(_accumulated
            .Select(kv => new AccumulatorSnapshot
            {
                Type = kv.Key.Item1,
                Scope = kv.Key.Item2,
                NetworkTier = kv.Key.Item3,
                // 0 = "take the limit from the plan" (AccumulatorWorkingSet.EnsureAccumulator).
                LimitAmount = 0m,
                AccumulatedAmountBefore = kv.Value,
                AccumulatedAmountAfter = kv.Value,
            })
            .ToList());

    public Task ApplyUpdatesAsync(string memberId, string subscriberId, Guid benefitPlanId, string planYear,
        string claimId, IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        foreach (var u in updates)
        {
            var key = (u.Type, u.Scope, u.NetworkTier);
            _accumulated[key] = _accumulated.GetValueOrDefault(key) + u.Amount;
            Applied.Add(u);
        }
        return Task.CompletedTask;
    }

    public Task ReverseAsync(string memberId, string subscriberId, Guid benefitPlanId, string planYear,
        string claimId, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct = default) => Task.CompletedTask;
}
