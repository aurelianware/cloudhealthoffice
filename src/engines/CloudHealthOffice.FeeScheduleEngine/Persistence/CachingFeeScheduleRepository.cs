using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// Per-process cache for adjudication-time fee schedule and provider contract reads.
/// Admin writes still go to the inner repository; short TTLs keep policy changes fresh.
/// </summary>
public sealed class CachingFeeScheduleRepository : IFeeScheduleRepository, IProviderContractRepository
{
    private static readonly TimeSpan ReadTtl = TimeSpan.FromMinutes(10);

    private readonly IFeeScheduleRepository _feeSchedules;
    private readonly IProviderContractRepository _contracts;
    private readonly IMemoryCache _cache;

    public CachingFeeScheduleRepository(
        IFeeScheduleRepository feeSchedules,
        IProviderContractRepository contracts,
        IMemoryCache cache)
    {
        _feeSchedules = feeSchedules;
        _contracts = contracts;
        _cache = cache;
    }

    public async Task<FeeSchedule?> GetByIdAsync(
        string tenantId,
        string id,
        CancellationToken ct = default)
    {
        var key = $"fee-schedule:id:{tenantId}:{id}";
        var cached = await GetOrCreateAsync(
            key,
            () => _feeSchedules.GetByIdAsync(tenantId, id, ct));
        return cached.Value;
    }

    public async Task<FeeSchedule?> GetDefaultForPlanAsync(
        string tenantId,
        string planId,
        DateTime serviceDate,
        CancellationToken ct = default)
    {
        var key = $"fee-schedule:default:{tenantId}:{planId}:{serviceDate.Date:yyyyMMdd}";
        var cached = await GetOrCreateAsync(
            key,
            () => _feeSchedules.GetDefaultForPlanAsync(tenantId, planId, serviceDate, ct));
        return cached.Value;
    }

    public Task<FeeScheduleLine?> GetLineAsync(
        string feeScheduleId,
        string procedureCode,
        string? modifier,
        CancellationToken ct = default)
        => _feeSchedules.GetLineAsync(feeScheduleId, procedureCode, modifier, ct);

    public async Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
    {
        var saved = await _feeSchedules.UpsertAsync(schedule, ct);
        _cache.Remove($"fee-schedule:id:{schedule.TenantId}:{schedule.Id}");
        return saved;
    }

    public Task<IReadOnlyList<FeeSchedule>> ListAsync(
        string tenantId,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
        => _feeSchedules.ListAsync(tenantId, page, pageSize, ct);

    public async Task<ProviderContract?> GetContractAsync(
        string tenantId,
        string providerNpi,
        string planId,
        DateTime serviceDate,
        CancellationToken ct = default)
    {
        var key = $"provider-contract:{tenantId}:{providerNpi}:{planId}:{serviceDate.Date:yyyyMMdd}";
        var cached = await GetOrCreateAsync(
            key,
            () => _contracts.GetContractAsync(tenantId, providerNpi, planId, serviceDate, ct));
        return cached.Value;
    }

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
        => _contracts.UpsertAsync(contract, ct);

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId,
        string providerNpi,
        CancellationToken ct = default)
        => _contracts.ListByProviderAsync(tenantId, providerNpi, ct);

    private async Task<CachedValue<T>> GetOrCreateAsync<T>(
        string key,
        Func<Task<T?>> factory)
    {
        var cached = await _cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ReadTtl;
            return new CachedValue<T>(await factory());
        });

        return cached ?? new CachedValue<T>(default);
    }

    private sealed record CachedValue<T>(T? Value);
}
