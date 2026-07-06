using System.Collections.Concurrent;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// Per-process cache for adjudication-time fee schedule and provider contract reads.
/// Admin writes still go to the inner repository; short TTLs keep policy changes fresh.
/// </summary>
public sealed class CachingFeeScheduleRepository : IFeeScheduleRepository, IProviderContractRepository
{
    private static readonly TimeSpan ReadHitTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReadMissTtl = TimeSpan.FromMinutes(1);

    private readonly IFeeScheduleRepository _feeSchedules;
    private readonly IProviderContractRepository _contracts;
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _defaultLookupKeys = new();
    private readonly ConcurrentDictionary<string, byte> _contractLookupKeys = new();

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
            () => _feeSchedules.GetDefaultForPlanAsync(tenantId, planId, serviceDate, ct),
            _defaultLookupKeys);
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
        InvalidateByPrefix(_defaultLookupKeys, $"fee-schedule:default:{schedule.TenantId}:");
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
            () => _contracts.GetContractAsync(tenantId, providerNpi, planId, serviceDate, ct),
            _contractLookupKeys);
        return cached.Value;
    }

    public async Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
    {
        var saved = await _contracts.UpsertAsync(contract, ct);
        InvalidateByPrefix(_contractLookupKeys, $"provider-contract:{contract.TenantId}:{contract.ProviderNpi}:{contract.PlanId}:");
        return saved;
    }

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId,
        string providerNpi,
        CancellationToken ct = default)
        => _contracts.ListByProviderAsync(tenantId, providerNpi, ct);

    private async Task<CachedValue<T>> GetOrCreateAsync<T>(
        string key,
        Func<Task<T?>> factory,
        ConcurrentDictionary<string, byte>? trackedKeys = null)
    {
        var cached = await _cache.GetOrCreateAsync(key, async entry =>
        {
            var value = await factory();
            entry.AbsoluteExpirationRelativeToNow = value is null ? ReadMissTtl : ReadHitTtl;
            if (trackedKeys is not null)
            {
                trackedKeys[key] = 0;
                entry.RegisterPostEvictionCallback(
                    static (evictedKey, _, _, state) =>
                    {
                        if (evictedKey is string removedKey &&
                            state is ConcurrentDictionary<string, byte> keys)
                        {
                            keys.TryRemove(removedKey, out _);
                        }
                    },
                    trackedKeys);
            }

            return new CachedValue<T>(value);
        });

        return cached ?? new CachedValue<T>(default);
    }

    private sealed record CachedValue<T>(T? Value);

    private void InvalidateByPrefix(ConcurrentDictionary<string, byte> trackedKeys, string prefix)
    {
        foreach (var key in trackedKeys.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            _cache.Remove(key);
            trackedKeys.TryRemove(key, out _);
        }
    }
}
