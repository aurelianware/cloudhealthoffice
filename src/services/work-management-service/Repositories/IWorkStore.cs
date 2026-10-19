using WorkManagementService.Models;

namespace WorkManagementService.Repositories;

/// <summary>Filter for the supervisor work list.</summary>
public sealed record WorkItemQuery(
    string? QueueId = null,
    WorkItemStatus? Status = null,
    string? AssignedTo = null,
    WorkType? WorkType = null,
    bool ActiveOnly = true,
    int Page = 1,
    int PageSize = 50);

/// <summary>Open / leased counts of one queue.</summary>
public sealed record QueueCounts(string QueueId, int Open, int Leased);

/// <summary>
/// Persistence for work management. EVERY method is tenant-scoped (the
/// tenant is a parameter or the record's own <c>TenantId</c>), so no lookup
/// can cross tenants by forgetting a filter.
///
/// <para><b>Concurrency.</b> Work items are written only through
/// <see cref="TryReplaceItemAsync"/>, a compare-and-swap on
/// <see cref="WorkItem.Version"/>: exactly one of several racing writers succeeds, the others
/// get <c>false</c> and must re-read. Get Next, release, renew, assign and
/// event application all go through it.</para>
/// </summary>
public interface IWorkStore
{
    // ── Queues ───────────────────────────────────────────────────────────
    Task<IReadOnlyList<WorkQueue>> ListQueuesAsync(string tenantId, CancellationToken ct = default);
    Task<WorkQueue?> GetQueueAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Inserts unless a queue with this id exists; returns whether this call created it.</summary>
    Task<bool> CreateQueueIfAbsentAsync(WorkQueue queue, CancellationToken ct = default);

    Task UpsertQueueAsync(WorkQueue queue, CancellationToken ct = default);
    Task<bool> DeleteQueueAsync(string tenantId, string id, CancellationToken ct = default);

    // ── Teams / memberships ──────────────────────────────────────────────
    Task<IReadOnlyList<WorkTeam>> ListTeamsAsync(string tenantId, CancellationToken ct = default);
    Task<WorkTeam?> GetTeamAsync(string tenantId, string id, CancellationToken ct = default);
    Task UpsertTeamAsync(WorkTeam team, CancellationToken ct = default);
    Task<bool> DeleteTeamAsync(string tenantId, string id, CancellationToken ct = default);

    Task<IReadOnlyList<WorkMembership>> ListMembershipsAsync(string tenantId, CancellationToken ct = default);
    Task<WorkMembership?> GetMembershipAsync(string tenantId, string id, CancellationToken ct = default);
    Task UpsertMembershipAsync(WorkMembership membership, CancellationToken ct = default);
    Task<bool> DeleteMembershipAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Tenants that have any work configuration (the periodic reconcile's tenant list).</summary>
    Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken ct = default);

    // ── Work items ───────────────────────────────────────────────────────
    Task<WorkItem?> GetItemAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Inserts a new item (Version 1); false when an item with this id already exists.</summary>
    Task<bool> TryCreateItemAsync(WorkItem item, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-swap: writes <paramref name="item"/> only if the stored
    /// item still has <paramref name="expectedVersion"/>. On success the item
    /// carries <c>expectedVersion + 1</c>.
    /// </summary>
    Task<bool> TryReplaceItemAsync(WorkItem item, long expectedVersion, CancellationToken ct = default);

    /// <summary>
    /// Get Next candidates in one queue: Open, or Leased with the lease run
    /// out before <paramref name="nowMs"/>; amount within
    /// <paramref name="maxAmount"/> when set; highest score first, then oldest.
    /// </summary>
    Task<IReadOnlyList<WorkItem>> FindCandidatesAsync(
        string tenantId, string queueId, decimal? maxAmount, long nowMs, int limit, CancellationToken ct = default);

    /// <summary>Items leased to <paramref name="userId"/> (any lease state), highest score first.</summary>
    Task<IReadOnlyList<WorkItem>> ListLeasedToAsync(string tenantId, string userId, CancellationToken ct = default);

    /// <summary>The supervisor list: filtered, highest score first, paged.</summary>
    Task<(IReadOnlyList<WorkItem> Items, int Total)> QueryItemsAsync(string tenantId, WorkItemQuery query, CancellationToken ct = default);

    /// <summary>Every Open / Leased item of a work type (recompute, reconcile).</summary>
    Task<IReadOnlyList<WorkItem>> ListActiveItemsAsync(string tenantId, WorkType type, CancellationToken ct = default);

    Task<IReadOnlyList<QueueCounts>> CountByQueueAsync(string tenantId, CancellationToken ct = default);

    // ── Audit ────────────────────────────────────────────────────────────
    Task AppendAuditAsync(WorkAuditEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<WorkAuditEvent>> ListAuditAsync(string tenantId, string workItemId, CancellationToken ct = default);
}
