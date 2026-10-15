using System.Text.Json;
using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

/// <summary>
/// Process-local store: Development / Testing hosts with no database
/// configured, and the test suites. Stores copies (as a database would), so a
/// caller can never mutate a stored row, and serializes every write under one
/// lock, so <see cref="TryReplaceItemAsync"/> is a true compare-and-swap.
/// Never used on a deployed host (Program refuses it).
/// </summary>
public sealed class InMemoryWorkStore : IWorkStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkQueue> _queues = new();
    private readonly Dictionary<string, WorkTeam> _teams = new();
    private readonly Dictionary<string, WorkMembership> _memberships = new();
    private readonly Dictionary<string, WorkItem> _items = new();
    private readonly List<WorkAuditEvent> _audit = new();

    public void Clear()
    {
        lock (_gate)
        {
            _queues.Clear();
            _teams.Clear();
            _memberships.Clear();
            _items.Clear();
            _audit.Clear();
        }
    }

    private static string Key(string tenantId, string id) => tenantId + "\u001f" + id;

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private Task<T> Locked<T>(Func<T> f)
    {
        lock (_gate) return Task.FromResult(f());
    }

    // ── Queues ───────────────────────────────────────────────────────────

    public Task<IReadOnlyList<WorkQueue>> ListQueuesAsync(string tenantId, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkQueue>>(() => _queues.Values.Where(q => q.TenantId == tenantId).Select(Clone).ToList());

    public Task<WorkQueue?> GetQueueAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _queues.TryGetValue(Key(tenantId, id), out var q) ? Clone(q) : null);

    public Task<bool> CreateQueueIfAbsentAsync(WorkQueue queue, CancellationToken ct = default) =>
        Locked(() => _queues.TryAdd(Key(queue.TenantId, queue.Id), Clone(queue)));

    public Task UpsertQueueAsync(WorkQueue queue, CancellationToken ct = default) =>
        Locked(() => _queues[Key(queue.TenantId, queue.Id)] = Clone(queue));

    public Task<bool> DeleteQueueAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _queues.Remove(Key(tenantId, id)));

    // ── Teams / memberships ──────────────────────────────────────────────

    public Task<IReadOnlyList<WorkTeam>> ListTeamsAsync(string tenantId, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkTeam>>(() => _teams.Values.Where(t => t.TenantId == tenantId).Select(Clone).ToList());

    public Task<WorkTeam?> GetTeamAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _teams.TryGetValue(Key(tenantId, id), out var t) ? Clone(t) : null);

    public Task UpsertTeamAsync(WorkTeam team, CancellationToken ct = default) =>
        Locked(() => _teams[Key(team.TenantId, team.Id)] = Clone(team));

    public Task<bool> DeleteTeamAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _teams.Remove(Key(tenantId, id)));

    public Task<IReadOnlyList<WorkMembership>> ListMembershipsAsync(string tenantId, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkMembership>>(() => _memberships.Values.Where(m => m.TenantId == tenantId).Select(Clone).ToList());

    public Task<WorkMembership?> GetMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _memberships.TryGetValue(Key(tenantId, id), out var m) ? Clone(m) : null);

    public Task UpsertMembershipAsync(WorkMembership membership, CancellationToken ct = default) =>
        Locked(() => _memberships[Key(membership.TenantId, membership.Id)] = Clone(membership));

    public Task<bool> DeleteMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _memberships.Remove(Key(tenantId, id)));

    public Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken ct = default) =>
        Locked<IReadOnlyList<string>>(() => _queues.Values.Select(q => q.TenantId)
            .Concat(_items.Values.Select(i => i.TenantId)).Distinct().ToList());

    // ── Work items ───────────────────────────────────────────────────────

    public Task<WorkItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default) =>
        Locked(() => _items.TryGetValue(Key(tenantId, id), out var i) ? Clone(i) : null);

    public Task<bool> TryCreateItemAsync(WorkItem item, CancellationToken ct = default) =>
        Locked(() =>
        {
            item.Version = 1;
            return _items.TryAdd(Key(item.TenantId, item.Id), Clone(item));
        });

    public Task<bool> TryReplaceItemAsync(WorkItem item, long expectedVersion, CancellationToken ct = default) =>
        Locked(() =>
        {
            var key = Key(item.TenantId, item.Id);
            if (!_items.TryGetValue(key, out var stored) || stored.Version != expectedVersion)
                return false;
            item.Version = expectedVersion + 1;
            _items[key] = Clone(item);
            return true;
        });

    public Task<IReadOnlyList<WorkItem>> FindCandidatesAsync(
        string tenantId, string queueId, decimal? maxAmount, long nowMs, int limit, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkItem>>(() => _items.Values
            .Where(i => i.TenantId == tenantId && i.QueueId == queueId)
            .Where(i => i.Status == WorkItemStatus.Open
                        || (i.Status == WorkItemStatus.Leased && (i.LeaseExpiresAtMs ?? 0) < nowMs))
            .Where(i => maxAmount is null || i.Attributes.Amount <= maxAmount)
            .OrderByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(Clone)
            .ToList());

    public Task<IReadOnlyList<WorkItem>> ListLeasedToAsync(string tenantId, string userId, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkItem>>(() => _items.Values
            .Where(i => i.TenantId == tenantId && i.Status == WorkItemStatus.Leased && i.AssignedTo == userId)
            .OrderByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt)
            .Select(Clone)
            .ToList());

    public Task<(IReadOnlyList<WorkItem> Items, int Total)> QueryItemsAsync(
        string tenantId, WorkItemQuery query, CancellationToken ct = default) =>
        Locked<(IReadOnlyList<WorkItem>, int)>(() =>
        {
            var matching = _items.Values.Where(i => i.TenantId == tenantId)
                .Where(i => query.QueueId is null || i.QueueId == query.QueueId)
                .Where(i => query.Status is null ? !query.ActiveOnly || i.IsActive : i.Status == query.Status)
                .Where(i => query.AssignedTo is null || i.AssignedTo == query.AssignedTo)
                .Where(i => query.WorkType is null || i.WorkType == query.WorkType)
                .OrderByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt)
                .ToList();
            var page = matching.Skip((Math.Max(query.Page, 1) - 1) * query.PageSize).Take(query.PageSize).Select(Clone).ToList();
            return (page, matching.Count);
        });

    public Task<IReadOnlyList<WorkItem>> ListActiveItemsAsync(string tenantId, WorkType type, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkItem>>(() => _items.Values
            .Where(i => i.TenantId == tenantId && i.WorkType == type && i.IsActive)
            .Select(Clone)
            .ToList());

    public Task<IReadOnlyList<QueueCounts>> CountByQueueAsync(string tenantId, CancellationToken ct = default) =>
        Locked<IReadOnlyList<QueueCounts>>(() => _items.Values
            .Where(i => i.TenantId == tenantId && i.IsActive)
            .GroupBy(i => i.QueueId)
            .Select(g => new QueueCounts(g.Key, g.Count(i => i.Status == WorkItemStatus.Open), g.Count(i => i.Status == WorkItemStatus.Leased)))
            .ToList());

    // ── Audit ────────────────────────────────────────────────────────────

    public Task AppendAuditAsync(WorkAuditEvent evt, CancellationToken ct = default) =>
        Locked(() =>
        {
            _audit.Add(Clone(evt));
            return true;
        });

    public Task<IReadOnlyList<WorkAuditEvent>> ListAuditAsync(string tenantId, string workItemId, CancellationToken ct = default) =>
        Locked<IReadOnlyList<WorkAuditEvent>>(() => _audit
            .Where(a => a.TenantId == tenantId && a.WorkItemId == workItemId)
            .OrderBy(a => a.At)
            .Select(Clone)
            .ToList());
}
