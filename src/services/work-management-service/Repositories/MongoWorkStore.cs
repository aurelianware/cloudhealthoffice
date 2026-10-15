using System.Collections.Concurrent;
using MongoDB.Driver;
using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

/// <summary>
/// MongoDB store. Work-item writes are <c>ReplaceOne</c> filtered on
/// (id, tenant, version): the server applies the filter and the write
/// atomically per document, so of N concurrent Get Next calls leasing the
/// same item exactly one matches and the rest see <c>MatchedCount == 0</c>.
/// </summary>
public sealed class MongoWorkStore : IWorkStore
{
    public const string QueuesCollection = "WorkQueues";
    public const string TeamsCollection = "WorkTeams";
    public const string MembershipsCollection = "WorkMemberships";
    public const string ItemsCollection = "WorkItems";
    public const string AuditCollection = "WorkAudit";

    private static readonly ConcurrentDictionary<string, bool> IndexedDatabases = new();

    private readonly IMongoCollection<WorkQueue> _queues;
    private readonly IMongoCollection<WorkTeam> _teams;
    private readonly IMongoCollection<WorkMembership> _memberships;
    private readonly IMongoCollection<WorkItem> _items;
    private readonly IMongoCollection<WorkAuditEvent> _audit;

    public MongoWorkStore(IMongoDatabase database)
    {
        _queues = database.GetCollection<WorkQueue>(QueuesCollection);
        _teams = database.GetCollection<WorkTeam>(TeamsCollection);
        _memberships = database.GetCollection<WorkMembership>(MembershipsCollection);
        _items = database.GetCollection<WorkItem>(ItemsCollection);
        _audit = database.GetCollection<WorkAuditEvent>(AuditCollection);

        // Once per database per process (the store may be scoped when Mongo
        // tenant scoping resolves a database per request).
        if (IndexedDatabases.TryAdd(database.DatabaseNamespace.DatabaseName, true))
            EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        var item = Builders<WorkItem>.IndexKeys;
        _items.Indexes.CreateMany(new[]
        {
            // Get Next: candidates of one queue by status, highest score first.
            new CreateIndexModel<WorkItem>(item.Ascending(i => i.TenantId).Ascending(i => i.QueueId)
                .Ascending(i => i.Status).Descending(i => i.PriorityScore)),
            // My Work.
            new CreateIndexModel<WorkItem>(item.Ascending(i => i.TenantId).Ascending(i => i.AssignedTo).Ascending(i => i.Status)),
            new CreateIndexModel<WorkItem>(item.Ascending(i => i.TenantId).Ascending(i => i.WorkType).Ascending(i => i.Status)),
        });
        _queues.Indexes.CreateOne(new CreateIndexModel<WorkQueue>(Builders<WorkQueue>.IndexKeys.Ascending(q => q.TenantId)));
        _teams.Indexes.CreateOne(new CreateIndexModel<WorkTeam>(Builders<WorkTeam>.IndexKeys.Ascending(t => t.TenantId)));
        _memberships.Indexes.CreateOne(new CreateIndexModel<WorkMembership>(
            Builders<WorkMembership>.IndexKeys.Ascending(m => m.TenantId).Ascending(m => m.UserId)));
        _audit.Indexes.CreateOne(new CreateIndexModel<WorkAuditEvent>(
            Builders<WorkAuditEvent>.IndexKeys.Ascending(a => a.TenantId).Ascending(a => a.WorkItemId).Ascending(a => a.At)));
    }

    // ── Queues ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkQueue>> ListQueuesAsync(string tenantId, CancellationToken ct = default) =>
        await _queues.Find(q => q.TenantId == tenantId).ToListAsync(ct);

    public async Task<WorkQueue?> GetQueueAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _queues.Find(q => q.TenantId == tenantId && q.Id == id).FirstOrDefaultAsync(ct);

    public async Task<bool> CreateQueueIfAbsentAsync(WorkQueue queue, CancellationToken ct = default)
    {
        try
        {
            await _queues.InsertOneAsync(queue, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public Task UpsertQueueAsync(WorkQueue queue, CancellationToken ct = default) =>
        _queues.ReplaceOneAsync(q => q.TenantId == queue.TenantId && q.Id == queue.Id, queue,
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<bool> DeleteQueueAsync(string tenantId, string id, CancellationToken ct = default) =>
        (await _queues.DeleteOneAsync(q => q.TenantId == tenantId && q.Id == id, ct)).DeletedCount > 0;

    // ── Teams / memberships ──────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkTeam>> ListTeamsAsync(string tenantId, CancellationToken ct = default) =>
        await _teams.Find(t => t.TenantId == tenantId).ToListAsync(ct);

    public async Task<WorkTeam?> GetTeamAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _teams.Find(t => t.TenantId == tenantId && t.Id == id).FirstOrDefaultAsync(ct);

    public Task UpsertTeamAsync(WorkTeam team, CancellationToken ct = default) =>
        _teams.ReplaceOneAsync(t => t.TenantId == team.TenantId && t.Id == team.Id, team, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<bool> DeleteTeamAsync(string tenantId, string id, CancellationToken ct = default) =>
        (await _teams.DeleteOneAsync(t => t.TenantId == tenantId && t.Id == id, ct)).DeletedCount > 0;

    public async Task<IReadOnlyList<WorkMembership>> ListMembershipsAsync(string tenantId, CancellationToken ct = default) =>
        await _memberships.Find(m => m.TenantId == tenantId).ToListAsync(ct);

    public async Task<WorkMembership?> GetMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _memberships.Find(m => m.TenantId == tenantId && m.Id == id).FirstOrDefaultAsync(ct);

    public Task UpsertMembershipAsync(WorkMembership membership, CancellationToken ct = default) =>
        _memberships.ReplaceOneAsync(m => m.TenantId == membership.TenantId && m.Id == membership.Id, membership,
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<bool> DeleteMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        (await _memberships.DeleteOneAsync(m => m.TenantId == tenantId && m.Id == id, ct)).DeletedCount > 0;

    public async Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken ct = default)
    {
        var fromQueues = await _queues.Distinct(q => q.TenantId, FilterDefinition<WorkQueue>.Empty, cancellationToken: ct).ToListAsync(ct);
        var fromItems = await _items.Distinct(i => i.TenantId, FilterDefinition<WorkItem>.Empty, cancellationToken: ct).ToListAsync(ct);
        return fromQueues.Concat(fromItems).Distinct(StringComparer.Ordinal).ToList();
    }

    // ── Work items ───────────────────────────────────────────────────────

    public async Task<WorkItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _items.Find(i => i.TenantId == tenantId && i.Id == id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryCreateItemAsync(WorkItem item, CancellationToken ct = default)
    {
        item.Version = 1;
        try
        {
            await _items.InsertOneAsync(item, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceItemAsync(WorkItem item, long expectedVersion, CancellationToken ct = default)
    {
        item.Version = expectedVersion + 1;
        var result = await _items.ReplaceOneAsync(
            i => i.TenantId == item.TenantId && i.Id == item.Id && i.Version == expectedVersion,
            item, new ReplaceOptions { IsUpsert = false }, ct);
        if (result.MatchedCount == 1) return true;
        item.Version = expectedVersion;
        return false;
    }

    public async Task<IReadOnlyList<WorkItem>> FindCandidatesAsync(
        string tenantId, string queueId, decimal? maxAmount, long nowMs, int limit, CancellationToken ct = default)
    {
        var f = Builders<WorkItem>.Filter;
        var filter = f.And(
            f.Eq(i => i.TenantId, tenantId),
            f.Eq(i => i.QueueId, queueId),
            f.Or(
                f.Eq(i => i.Status, WorkItemStatus.Open),
                f.And(f.Eq(i => i.Status, WorkItemStatus.Leased), f.Lt(i => i.LeaseExpiresAtMs, nowMs))));
        if (maxAmount is { } max)
            filter = f.And(filter, f.Lte(i => i.Attributes.Amount, max));
        return await _items.Find(filter)
            .SortByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkItem>> ListLeasedToAsync(string tenantId, string userId, CancellationToken ct = default) =>
        await _items.Find(i => i.TenantId == tenantId && i.AssignedTo == userId && i.Status == WorkItemStatus.Leased)
            .SortByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt)
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<WorkItem> Items, int Total)> QueryItemsAsync(
        string tenantId, WorkItemQuery query, CancellationToken ct = default)
    {
        var f = Builders<WorkItem>.Filter;
        var filter = f.Eq(i => i.TenantId, tenantId);
        if (query.QueueId is not null) filter &= f.Eq(i => i.QueueId, query.QueueId);
        if (query.Status is { } status) filter &= f.Eq(i => i.Status, status);
        else if (query.ActiveOnly) filter &= f.In(i => i.Status, new[] { WorkItemStatus.Open, WorkItemStatus.Leased });
        if (query.AssignedTo is not null) filter &= f.Eq(i => i.AssignedTo, query.AssignedTo);
        if (query.WorkType is { } type) filter &= f.Eq(i => i.WorkType, type);

        var total = (int)await _items.CountDocumentsAsync(filter, cancellationToken: ct);
        var items = await _items.Find(filter)
            .SortByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt)
            .Skip((Math.Max(query.Page, 1) - 1) * query.PageSize)
            .Limit(query.PageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<WorkItem>> ListActiveItemsAsync(string tenantId, WorkType type, CancellationToken ct = default)
    {
        var f = Builders<WorkItem>.Filter;
        return await _items.Find(f.And(
                f.Eq(i => i.TenantId, tenantId),
                f.Eq(i => i.WorkType, type),
                f.In(i => i.Status, new[] { WorkItemStatus.Open, WorkItemStatus.Leased })))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<QueueCounts>> CountByQueueAsync(string tenantId, CancellationToken ct = default)
    {
        var f = Builders<WorkItem>.Filter;
        var rows = await _items.Aggregate()
            .Match(f.And(f.Eq(i => i.TenantId, tenantId), f.In(i => i.Status, new[] { WorkItemStatus.Open, WorkItemStatus.Leased })))
            .Group(i => new { i.QueueId, i.Status }, g => new { g.Key.QueueId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.QueueId)
            .Select(g => new QueueCounts(
                g.Key,
                g.Where(r => r.Status == WorkItemStatus.Open).Sum(r => r.Count),
                g.Where(r => r.Status == WorkItemStatus.Leased).Sum(r => r.Count)))
            .ToList();
    }

    // ── Audit ────────────────────────────────────────────────────────────

    public Task AppendAuditAsync(WorkAuditEvent evt, CancellationToken ct = default) =>
        _audit.InsertOneAsync(evt, cancellationToken: ct);

    public async Task<IReadOnlyList<WorkAuditEvent>> ListAuditAsync(string tenantId, string workItemId, CancellationToken ct = default) =>
        await _audit.Find(a => a.TenantId == tenantId && a.WorkItemId == workItemId).SortBy(a => a.At).ThenBy(a => a.Seq).ToListAsync(ct);
}
