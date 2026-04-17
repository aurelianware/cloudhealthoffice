using MemberService.Models;
using MemberService.Repositories;

namespace MemberService.Tests.Fakes;

/// <summary>In-memory repository with the same idempotency and ordering semantics as prod.</summary>
public sealed class InMemoryMemberEventRepository : IMemberEventRepository
{
    private readonly List<MemberEvent> _events = new();
    private readonly object _lock = new();

    public IReadOnlyList<MemberEvent> All
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public Task<AppendResult> AppendAsync(MemberEvent evt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var existing = _events.FirstOrDefault(e =>
                e.TenantId == evt.TenantId && e.MemberId == evt.MemberId && e.EventId == evt.EventId);
            if (existing != null) return Task.FromResult(new AppendResult(existing, false));

            var versionConflict = _events.Any(e =>
                e.TenantId == evt.TenantId && e.MemberId == evt.MemberId && e.Version == evt.Version);
            if (versionConflict) return Task.FromResult(new AppendResult(evt, false));

            if (string.IsNullOrEmpty(evt.PartitionKey))
                evt.PartitionKey = MemberEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
            if (string.IsNullOrEmpty(evt.Id)) evt.Id = evt.EventId;

            _events.Add(evt);
            return Task.FromResult(new AppendResult(evt, true));
        }
    }

    public Task<IReadOnlyList<MemberEvent>> ListByMemberAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var list = _events
                .Where(e => e.TenantId == tenantId && e.MemberId == memberId)
                .OrderBy(e => e.Version)
                .ToList();
            return Task.FromResult<IReadOnlyList<MemberEvent>>(list);
        }
    }

    public Task<MemberEvent?> GetByIdAsync(string tenantId, string memberId, string eventId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var match = _events.FirstOrDefault(e =>
                e.TenantId == tenantId && e.MemberId == memberId && e.EventId == eventId);
            return Task.FromResult<MemberEvent?>(match);
        }
    }

    public Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var max = _events
                .Where(e => e.TenantId == tenantId && e.MemberId == memberId)
                .Select(e => (int?)e.Version)
                .Max();
            return Task.FromResult((max ?? 0) + 1);
        }
    }
}
