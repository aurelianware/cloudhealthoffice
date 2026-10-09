using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;
using EnginePersistence = CloudHealthOffice.FeeScheduleEngine.Persistence;

namespace CloudHealthOffice.PricingApi.Services.Engine;

/// <summary>
/// The engine's repositories for one repricing request. The Pricing API prices
/// against a fee schedule the caller names, not a provider contract, so the
/// contract here is the plain one the caller implies: every code priced from
/// the named schedule, no carve-outs, no lesser-of-billed provision. Under that
/// contract the engine's resolution (schedule → rate line → base amount →
/// modifiers → units → multiple procedure ranking → per-stay allocation) is
/// exactly the one adjudication runs.
/// </summary>
internal sealed class RepricingScheduleStore
    : EnginePersistence.IFeeScheduleRepository, EnginePersistence.IProviderContractRepository
{
    internal const string TenantId = "pricing-api";
    internal const string ProviderNpi = "0000000000";
    internal const string PlanId = "pricing-api";

    private readonly IPricingScheduleSource _source;
    private readonly EngineModels.FeeSchedule _schedule;

    public RepricingScheduleStore(IPricingScheduleSource source, EngineModels.FeeSchedule schedule)
    {
        _source = source;
        _schedule = schedule;
    }

    public Task<EngineModels.FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
        => string.Equals(id, _schedule.Id, StringComparison.Ordinal)
            ? Task.FromResult<EngineModels.FeeSchedule?>(_schedule)
            : _source.GetReferencedAsync(id, ct);

    /// <summary>No plan default: a code missing from the named schedule is not priced from another one.</summary>
    public Task<EngineModels.FeeSchedule?> GetDefaultForPlanAsync(
        string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
        => Task.FromResult<EngineModels.FeeSchedule?>(null);

    public Task<EngineModels.FeeScheduleLine?> GetLineAsync(
        string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
        => Task.FromResult<EngineModels.FeeScheduleLine?>(null);

    public Task<EngineModels.FeeSchedule> UpsertAsync(EngineModels.FeeSchedule schedule, CancellationToken ct = default)
        => throw new NotSupportedException("Repricing is read-only.");

    public Task<IReadOnlyList<EngineModels.FeeSchedule>> ListAsync(
        string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EngineModels.FeeSchedule>>([_schedule]);

    public Task<EngineModels.ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate, CancellationToken ct = default)
        => Task.FromResult<EngineModels.ProviderContract?>(new EngineModels.ProviderContract
        {
            Id = $"pricing-api:{_schedule.Id}",
            TenantId = tenantId,
            ProviderNpi = providerNpi,
            PlanId = planId,
            NetworkStatus = EngineDomain.NetworkStatus.InNetwork,
            FeeScheduleId = _schedule.Id,
            EffectiveDate = DateTime.MinValue,
            LesserOfBilledCharges = false,
        });

    public Task<EngineModels.ProviderContract> UpsertAsync(EngineModels.ProviderContract contract, CancellationToken ct = default)
        => throw new NotSupportedException("Repricing is read-only.");

    public Task<IReadOnlyList<EngineModels.ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EngineModels.ProviderContract>>([]);
}
