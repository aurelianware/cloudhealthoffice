using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

/// <summary>Container names (each partitioned on <c>/tenantId</c>).</summary>
public sealed class CosmosWorkStoreOptions
{
    public string DatabaseName { get; set; } = "CloudHealthOffice";
    public string QueuesContainer { get; set; } = "WorkQueues";
    public string TeamsContainer { get; set; } = "WorkTeams";
    public string MembershipsContainer { get; set; } = "WorkMemberships";
    public string ItemsContainer { get; set; } = "WorkItems";
    public string AuditContainer { get; set; } = "WorkAudit";
    public string NoticesContainer { get; set; } = "PlanNotices";
    public string NoticeReadsContainer { get; set; } = "PlanNoticeReads";
    public string PinsContainer { get; set; } = "PinnedRecords";

    public static CosmosWorkStoreOptions From(IConfiguration configuration) => new()
    {
        DatabaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice",
        QueuesContainer = configuration["CosmosDb:WorkQueuesContainer"] ?? "WorkQueues",
        TeamsContainer = configuration["CosmosDb:WorkTeamsContainer"] ?? "WorkTeams",
        MembershipsContainer = configuration["CosmosDb:WorkMembershipsContainer"] ?? "WorkMemberships",
        ItemsContainer = configuration["CosmosDb:WorkItemsContainer"] ?? "WorkItems",
        AuditContainer = configuration["CosmosDb:WorkAuditContainer"] ?? "WorkAudit",
        NoticesContainer = configuration["CosmosDb:PlanNoticesContainer"] ?? "PlanNotices",
        NoticeReadsContainer = configuration["CosmosDb:PlanNoticeReadsContainer"] ?? "PlanNoticeReads",
        PinsContainer = configuration["CosmosDb:PinnedRecordsContainer"] ?? "PinnedRecords",
    };

    public IEnumerable<string> AllContainers() =>
        [QueuesContainer, TeamsContainer, MembershipsContainer, ItemsContainer, AuditContainer,
         NoticesContainer, NoticeReadsContainer, PinsContainer];
}

/// <summary>
/// Cosmos DB store (System.Text.Json serializer, camelCase, enums as
/// camelCase strings). Work-item writes are <c>ReplaceItem</c> with
/// <c>IfMatchEtag</c> = the document <c>_etag</c> read with the item: any
/// write in between changes the ETag and the replace fails with 412, so of N
/// concurrent Get Next calls leasing one item exactly one succeeds.
/// </summary>
public sealed class CosmosWorkStore : IWorkStore
{
    private readonly Container _queues;
    private readonly Container _teams;
    private readonly Container _memberships;
    private readonly Container _items;
    private readonly Container _audit;

    public CosmosWorkStore(CosmosClient client, CosmosWorkStoreOptions options)
    {
        _queues = client.GetContainer(options.DatabaseName, options.QueuesContainer);
        _teams = client.GetContainer(options.DatabaseName, options.TeamsContainer);
        _memberships = client.GetContainer(options.DatabaseName, options.MembershipsContainer);
        _items = client.GetContainer(options.DatabaseName, options.ItemsContainer);
        _audit = client.GetContainer(options.DatabaseName, options.AuditContainer);
    }

    /// <summary>Creates the database and containers when absent (development, the emulator tests).</summary>
    public static async Task EnsureContainersAsync(CosmosClient client, CosmosWorkStoreOptions options, CancellationToken ct = default)
    {
        var db = (await client.CreateDatabaseIfNotExistsAsync(options.DatabaseName, cancellationToken: ct)).Database;
        foreach (var name in options.AllContainers())
            await db.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/tenantId"), cancellationToken: ct);
    }

    private static string Wire<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static async Task<List<T>> QueryAsync<T>(Container container, QueryDefinition query, string? tenantId, CancellationToken ct, int? max = null)
    {
        var options = new QueryRequestOptions();
        if (tenantId is not null) options.PartitionKey = new PartitionKey(tenantId);
        if (max is { } m) options.MaxItemCount = m;
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
            if (max is { } limit && results.Count >= limit) break;
        }
        return results;
    }

    private static async Task<T?> ReadAsync<T>(Container container, string tenantId, string id, CancellationToken ct) where T : class
    {
        try
        {
            return (await container.ReadItemAsync<T>(id, new PartitionKey(tenantId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<bool> DeleteAsync<T>(Container container, string tenantId, string id, CancellationToken ct)
    {
        try
        {
            await container.DeleteItemAsync<T>(id, new PartitionKey(tenantId), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static QueryDefinition ByTenant(string tenantId) =>
        new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @t").WithParameter("@t", tenantId);

    // ── Queues ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkQueue>> ListQueuesAsync(string tenantId, CancellationToken ct = default) =>
        await QueryAsync<WorkQueue>(_queues, ByTenant(tenantId), tenantId, ct);

    public Task<WorkQueue?> GetQueueAsync(string tenantId, string id, CancellationToken ct = default) =>
        ReadAsync<WorkQueue>(_queues, tenantId, id, ct);

    public async Task<bool> CreateQueueIfAbsentAsync(WorkQueue queue, CancellationToken ct = default)
    {
        try
        {
            await _queues.CreateItemAsync(queue, new PartitionKey(queue.TenantId), cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public Task UpsertQueueAsync(WorkQueue queue, CancellationToken ct = default) =>
        _queues.UpsertItemAsync(queue, new PartitionKey(queue.TenantId), cancellationToken: ct);

    public Task<bool> DeleteQueueAsync(string tenantId, string id, CancellationToken ct = default) =>
        DeleteAsync<WorkQueue>(_queues, tenantId, id, ct);

    // ── Teams / memberships ──────────────────────────────────────────────

    public async Task<IReadOnlyList<WorkTeam>> ListTeamsAsync(string tenantId, CancellationToken ct = default) =>
        await QueryAsync<WorkTeam>(_teams, ByTenant(tenantId), tenantId, ct);

    public Task<WorkTeam?> GetTeamAsync(string tenantId, string id, CancellationToken ct = default) =>
        ReadAsync<WorkTeam>(_teams, tenantId, id, ct);

    public Task UpsertTeamAsync(WorkTeam team, CancellationToken ct = default) =>
        _teams.UpsertItemAsync(team, new PartitionKey(team.TenantId), cancellationToken: ct);

    public Task<bool> DeleteTeamAsync(string tenantId, string id, CancellationToken ct = default) =>
        DeleteAsync<WorkTeam>(_teams, tenantId, id, ct);

    public async Task<IReadOnlyList<WorkMembership>> ListMembershipsAsync(string tenantId, CancellationToken ct = default) =>
        await QueryAsync<WorkMembership>(_memberships, ByTenant(tenantId), tenantId, ct);

    public Task<WorkMembership?> GetMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        ReadAsync<WorkMembership>(_memberships, tenantId, id, ct);

    public Task UpsertMembershipAsync(WorkMembership membership, CancellationToken ct = default) =>
        _memberships.UpsertItemAsync(membership, new PartitionKey(membership.TenantId), cancellationToken: ct);

    public Task<bool> DeleteMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        DeleteAsync<WorkMembership>(_memberships, tenantId, id, ct);

    public async Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT DISTINCT VALUE c.tenantId FROM c");
        var fromQueues = await QueryAsync<string>(_queues, query, null, ct);
        var fromItems = await QueryAsync<string>(_items, query, null, ct);
        return fromQueues.Concat(fromItems).Distinct(StringComparer.Ordinal).ToList();
    }

    // ── Work items ───────────────────────────────────────────────────────

    public Task<WorkItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default) =>
        ReadAsync<WorkItem>(_items, tenantId, id, ct);

    public async Task<bool> TryCreateItemAsync(WorkItem item, CancellationToken ct = default)
    {
        item.Version = 1;
        item.StoreETag = null;
        try
        {
            var response = await _items.CreateItemAsync(item, new PartitionKey(item.TenantId), cancellationToken: ct);
            item.StoreETag = response.ETag;
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceItemAsync(WorkItem item, long expectedVersion, CancellationToken ct = default)
    {
        var etag = item.StoreETag;
        if (etag is null)
        {
            // Not read through this store (no ETag): compare the version, then
            // fence on the ETag of what was just compared.
            var current = await GetItemAsync(item.TenantId, item.Id, ct);
            if (current is null || current.Version != expectedVersion) return false;
            etag = current.StoreETag;
        }

        item.Version = expectedVersion + 1;
        try
        {
            var response = await _items.ReplaceItemAsync(item, item.Id, new PartitionKey(item.TenantId),
                new ItemRequestOptions { IfMatchEtag = etag }, ct);
            item.StoreETag = response.ETag;
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            item.Version = expectedVersion;
            return false;
        }
    }

    public async Task<IReadOnlyList<WorkItem>> FindCandidatesAsync(
        string tenantId, string queueId, decimal? maxAmount, long nowMs, int limit, CancellationToken ct = default)
    {
        var sql = "SELECT TOP @limit * FROM c WHERE c.tenantId = @t AND c.queueId = @q " +
                  "AND (c.status = @open OR (c.status = @leased AND c.leaseExpiresAtMs < @now))" +
                  (maxAmount is null ? string.Empty : " AND c.attributes.amount <= @max") +
                  " ORDER BY c.priorityScore DESC";
        var query = new QueryDefinition(sql)
            .WithParameter("@limit", limit)
            .WithParameter("@t", tenantId)
            .WithParameter("@q", queueId)
            .WithParameter("@open", Wire(WorkItemStatus.Open))
            .WithParameter("@leased", Wire(WorkItemStatus.Leased))
            .WithParameter("@now", nowMs);
        if (maxAmount is { } max) query = query.WithParameter("@max", max);
        var items = await QueryAsync<WorkItem>(_items, query, tenantId, ct);
        // Ties (equal scores): oldest first, as in the other stores.
        return items.OrderByDescending(i => i.PriorityScore).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<WorkItem>> ListLeasedToAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @t AND c.assignedTo = @u AND c.status = @leased ORDER BY c.priorityScore DESC")
            .WithParameter("@t", tenantId).WithParameter("@u", userId).WithParameter("@leased", Wire(WorkItemStatus.Leased));
        return await QueryAsync<WorkItem>(_items, query, tenantId, ct);
    }

    public async Task<(IReadOnlyList<WorkItem> Items, int Total)> QueryItemsAsync(
        string tenantId, WorkItemQuery query, CancellationToken ct = default)
    {
        var where = new List<string> { "c.tenantId = @t" };
        var parameters = new Dictionary<string, object> { ["@t"] = tenantId };
        if (query.QueueId is not null) { where.Add("c.queueId = @q"); parameters["@q"] = query.QueueId; }
        if (query.Status is { } status) { where.Add("c.status = @s"); parameters["@s"] = Wire(status); }
        else if (query.ActiveOnly)
        {
            where.Add("(c.status = @open OR c.status = @leased)");
            parameters["@open"] = Wire(WorkItemStatus.Open);
            parameters["@leased"] = Wire(WorkItemStatus.Leased);
        }
        if (query.AssignedTo is not null) { where.Add("c.assignedTo = @u"); parameters["@u"] = query.AssignedTo; }
        if (query.WorkType is { } type) { where.Add("c.workType = @w"); parameters["@w"] = Wire(type); }

        var clause = string.Join(" AND ", where);
        QueryDefinition Build(string sql)
        {
            var q = new QueryDefinition(sql);
            foreach (var (k, v) in parameters) q = q.WithParameter(k, v);
            return q;
        }

        var total = (await QueryAsync<int>(_items, Build($"SELECT VALUE COUNT(1) FROM c WHERE {clause}"), tenantId, ct)).FirstOrDefault();
        var skip = (Math.Max(query.Page, 1) - 1) * query.PageSize;
        var items = await QueryAsync<WorkItem>(_items,
            Build($"SELECT * FROM c WHERE {clause} ORDER BY c.priorityScore DESC OFFSET {skip} LIMIT {query.PageSize}"), tenantId, ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<WorkItem>> ListActiveItemsAsync(string tenantId, WorkType type, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @t AND c.workType = @w AND (c.status = @open OR c.status = @leased)")
            .WithParameter("@t", tenantId).WithParameter("@w", Wire(type))
            .WithParameter("@open", Wire(WorkItemStatus.Open)).WithParameter("@leased", Wire(WorkItemStatus.Leased));
        return await QueryAsync<WorkItem>(_items, query, tenantId, ct);
    }

    private sealed class QueueStatusRow
    {
        public string QueueId { get; set; } = string.Empty;
        public WorkItemStatus Status { get; set; }
    }

    public async Task<IReadOnlyList<QueueCounts>> CountByQueueAsync(string tenantId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
                "SELECT c.queueId, c.status FROM c WHERE c.tenantId = @t AND (c.status = @open OR c.status = @leased)")
            .WithParameter("@t", tenantId)
            .WithParameter("@open", Wire(WorkItemStatus.Open)).WithParameter("@leased", Wire(WorkItemStatus.Leased));
        var rows = await QueryAsync<QueueStatusRow>(_items, query, tenantId, ct);
        return rows.GroupBy(r => r.QueueId)
            .Select(g => new QueueCounts(g.Key, g.Count(r => r.Status == WorkItemStatus.Open), g.Count(r => r.Status == WorkItemStatus.Leased)))
            .ToList();
    }

    // ── Audit ────────────────────────────────────────────────────────────

    public Task AppendAuditAsync(WorkAuditEvent evt, CancellationToken ct = default) =>
        _audit.CreateItemAsync(evt, new PartitionKey(evt.TenantId), cancellationToken: ct);

    public async Task<IReadOnlyList<WorkAuditEvent>> ListAuditAsync(string tenantId, string workItemId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @t AND c.workItemId = @w")
            .WithParameter("@t", tenantId).WithParameter("@w", workItemId);
        return (await QueryAsync<WorkAuditEvent>(_audit, query, tenantId, ct)).OrderBy(a => a.At).ThenBy(a => a.Seq).ToList();
    }
}
