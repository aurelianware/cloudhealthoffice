using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

/// <summary>
/// Plan notices, read marks and pinned records (Home). Every method is
/// tenant-scoped; pins and read marks are also scoped to their user.
/// </summary>
public interface IWorkspaceStore
{
    Task<IReadOnlyList<PlanNotice>> ListNoticesAsync(string tenantId, CancellationToken ct = default);
    Task<PlanNotice?> GetNoticeAsync(string tenantId, string id, CancellationToken ct = default);
    Task UpsertNoticeAsync(PlanNotice notice, CancellationToken ct = default);

    Task<IReadOnlySet<string>> ReadNoticeIdsAsync(string tenantId, string userId, CancellationToken ct = default);
    Task MarkReadAsync(NoticeRead read, CancellationToken ct = default);

    Task<IReadOnlyList<PinnedRecord>> ListPinsAsync(string tenantId, string userId, CancellationToken ct = default);
    Task UpsertPinAsync(PinnedRecord pin, CancellationToken ct = default);
    Task<bool> DeletePinAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class InMemoryWorkspaceStore : IWorkspaceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), PlanNotice> _notices = new();
    private readonly Dictionary<(string, string), NoticeRead> _reads = new();
    private readonly Dictionary<(string, string), PinnedRecord> _pins = new();

    public void Clear()
    {
        lock (_gate) { _notices.Clear(); _reads.Clear(); _pins.Clear(); }
    }

    public Task<IReadOnlyList<PlanNotice>> ListNoticesAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PlanNotice>>(_notices.Values.Where(n => n.TenantId == tenantId).ToList());
    }

    public Task<PlanNotice?> GetNoticeAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_notices.GetValueOrDefault((tenantId, id)));
    }

    public Task UpsertNoticeAsync(PlanNotice notice, CancellationToken ct = default)
    {
        lock (_gate) _notices[(notice.TenantId, notice.Id)] = notice;
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> ReadNoticeIdsAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlySet<string>>(
                _reads.Values.Where(r => r.TenantId == tenantId && r.UserId == userId).Select(r => r.NoticeId).ToHashSet());
    }

    public Task MarkReadAsync(NoticeRead read, CancellationToken ct = default)
    {
        lock (_gate) _reads.TryAdd((read.TenantId, read.Id), read);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PinnedRecord>> ListPinsAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PinnedRecord>>(
                _pins.Values.Where(p => p.TenantId == tenantId && p.UserId == userId).ToList());
    }

    public Task UpsertPinAsync(PinnedRecord pin, CancellationToken ct = default)
    {
        lock (_gate) _pins[(pin.TenantId, pin.Id)] = pin;
        return Task.CompletedTask;
    }

    public Task<bool> DeletePinAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_pins.Remove((tenantId, id)));
    }
}
