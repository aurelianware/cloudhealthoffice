using CoverageService.Models.Tpl;
using CoverageService.Repositories;

namespace CoverageService.Tests.Tpl;

internal sealed class InMemoryTplRepository : ITplPolicyRepository
{
    private readonly Dictionary<string, TplPolicy> _rows = new();
    public IEnumerable<TplPolicy> Rows => _rows.Values;

    public void Seed(TplPolicy p) => _rows[p.Id] = p;

    public Task<IReadOnlyList<TplPolicy>> GetByMemberAsync(string tenantId, string memberId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values.Where(r => r.TenantId == tenantId && r.MemberId == memberId).ToList());

    public Task<IReadOnlyList<TplPolicy>> GetByMembersAsync(string tenantId, string state, IReadOnlyCollection<string> memberIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values
            .Where(r => r.TenantId == tenantId && r.State == state && memberIds.Contains(r.MemberId)).ToList());

    public Task<IReadOnlyList<TplPolicy>> GetActiveBySourceAsync(string tenantId, string state, string source, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values
            .Where(r => r.TenantId == tenantId && r.State == state && r.Source == source && r.Status == TplPolicyStatus.Active).ToList());

    public Task<TplPolicy?> GetByIdAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_rows.Values.FirstOrDefault(r => r.TenantId == tenantId && r.Id == id));

    public Task<IReadOnlyList<TplPolicy>> GetStateReportRowsAsync(string tenantId, string state, bool includeReported, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TplPolicy>>(_rows.Values.Where(r => r.TenantId == tenantId && r.State == state
            && (r.PendingStateAction != null || (includeReported && r.ReportedToStateAt != null))).ToList());

    public Task UpsertManyAsync(IReadOnlyCollection<TplPolicy> policies, CancellationToken ct = default)
    {
        foreach (var p in policies) _rows[p.Id] = p;
        return Task.CompletedTask;
    }
}
