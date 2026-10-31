using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;

namespace BenefitPlanService.Tests.Fakes;

/// <summary>Fee schedule store for unit tests; upserts are revision-checked like the MongoDB store.</summary>
public sealed class InMemoryFeeScheduleRepository : IFeeScheduleRepository
{
    private readonly Dictionary<(string, string), FeeSchedule> _docs = new();
    public IReadOnlyList<FeeSchedule> Stored => _docs.Values.ToList();
    public int Writes { get; private set; }

    public Task<FeeSchedule?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult(_docs.GetValueOrDefault((tenantId, id)));

    public Task<FeeSchedule?> GetDefaultForPlanAsync(string tenantId, string planId, DateTime serviceDate, CancellationToken ct = default)
        => Task.FromResult(_docs.Values.FirstOrDefault(s => s.TenantId == tenantId && s.DefaultForPlanId == planId));

    public Task<FeeScheduleLine?> GetLineAsync(string feeScheduleId, string procedureCode, string? modifier, CancellationToken ct = default)
        => Task.FromResult<FeeScheduleLine?>(null);

    public Task<FeeSchedule> UpsertAsync(FeeSchedule schedule, CancellationToken ct = default)
    {
        var stored = _docs.GetValueOrDefault((schedule.TenantId, schedule.Id))?.Revision ?? 0;
        if (stored != schedule.Revision)
            throw new FeeScheduleConcurrencyException("Fee schedule", schedule.Id, schedule.Revision);
        schedule.Revision++;
        _docs[(schedule.TenantId, schedule.Id)] = schedule;
        Writes++;
        return Task.FromResult(schedule);
    }

    public Task<IReadOnlyList<FeeSchedule>> ListAsync(string tenantId, int page = 1, int pageSize = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FeeSchedule>>(_docs.Values.Where(s => s.TenantId == tenantId).ToList());
}
