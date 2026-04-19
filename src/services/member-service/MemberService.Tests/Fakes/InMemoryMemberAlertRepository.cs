using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>In-memory <see cref="IMemberAlertRepository"/> for tests.</summary>
public sealed class InMemoryMemberAlertRepository : IMemberAlertRepository
{
    public List<MemberAlert> Alerts { get; } = new();
    private readonly object _lock = new();

    public Task<MemberAlert> CreateAsync(MemberAlert alert)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(alert.Id)) alert.Id = Guid.NewGuid().ToString();
            if (alert.CreatedDate == default) alert.CreatedDate = DateTime.UtcNow;
            Alerts.Add(alert);
            return Task.FromResult(alert);
        }
    }

    public Task<MemberAlert?> GetByIdAsync(string tenantId, string memberId, string alertId)
    {
        lock (_lock)
        {
            var hit = Alerts.FirstOrDefault(a =>
                a.TenantId == tenantId && a.MemberId == memberId && a.Id == alertId);
            return Task.FromResult<MemberAlert?>(hit);
        }
    }

    public Task<IReadOnlyList<MemberAlert>> ListByMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly,
        DateTime? asOf = null)
    {
        lock (_lock)
        {
            var t = asOf ?? DateTime.UtcNow;
            var q = Alerts.Where(a => a.TenantId == tenantId && a.MemberId == memberId);
            if (activeOnly) q = q.Where(a => a.IsActive(t));
            var list = q.OrderByDescending(a => a.StartDate)
                        .ThenBy(a => a.AlertType)
                        .ToList();
            return Task.FromResult<IReadOnlyList<MemberAlert>>(list);
        }
    }

    public Task<MemberAlert> EndAsync(MemberAlert alert)
    {
        lock (_lock)
        {
            var idx = Alerts.FindIndex(a => a.TenantId == alert.TenantId && a.Id == alert.Id);
            if (idx >= 0) Alerts[idx] = alert;
            return Task.FromResult(alert);
        }
    }
}
