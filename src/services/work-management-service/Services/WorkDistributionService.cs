using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Options;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Repositories;

namespace WorkManagementService.Services;

/// <summary>What a distribution action did.</summary>
public enum DistributionStatus
{
    Ok,
    NotFound,

    /// <summary>The caller may not act on this item (not its holder).</summary>
    Forbidden,

    /// <summary>The item is not in a state the action applies to (done, leased by someone else ...).</summary>
    Conflict,
}

public sealed record DistributionResult(DistributionStatus Status, WorkItem? Item = null, string? Error = null, bool Resumed = false);

/// <summary>
/// Distribution: pull (Get Next, renew, release) and push (assign, reassign,
/// unassign). Every state change is a compare-and-swap on the item's version,
/// so two callers can never both take the same item: the loser re-reads and
/// moves on to the next candidate.
/// </summary>
public sealed class WorkDistributionService
{
    private readonly IWorkStore _store;
    private readonly WorkConfigService _config;
    private readonly WorkManagementOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkDistributionService> _logger;

    public WorkDistributionService(
        IWorkStore store, WorkConfigService config, IOptions<WorkManagementOptions> options, TimeProvider time,
        ILogger<WorkDistributionService> logger)
    {
        _store = store;
        _config = config;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>
    /// Get Next for <paramref name="actor"/>:
    /// <list type="number">
    ///   <item>the caller's own active lease first (highest score) — a person
    ///   finishes what they hold before taking more;</item>
    ///   <item>otherwise the highest-scoring eligible item of the caller's
    ///   primary queues, then of the overflow queues. Eligible: Open, or
    ///   Leased with the lease run out (reclaimed); amount within the
    ///   membership's authority limit; queue's work type permitted by the
    ///   caller's role (entitlement);</item>
    ///   <item>leased with a conditional write; on conflict, the next
    ///   candidate. Candidates are re-read up to
    ///   <see cref="WorkManagementOptions.GetNextRounds"/> times.</item>
    /// </list>
    /// Null when nothing is eligible.
    /// </summary>
    public async Task<DistributionResult?> GetNextAsync(ICurrentActor actor, WorkType? workType, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        var userId = actor.UserId;
        var now = Now;

        var mine = (await _store.ListLeasedToAsync(tenantId, userId, ct))
            .Where(i => i.HasActiveLease(now) && (workType is null || i.WorkType == workType))
            .Where(i => WorkEntitlements.Permits(actor, i.WorkType))
            .FirstOrDefault();
        if (mine is not null)
            return new DistributionResult(DistributionStatus.Ok, mine, Resumed: true);

        var effective = (await _config.EffectiveQueuesAsync(tenantId, userId, ct))
            .Where(e => workType is null || e.Queue.WorkType == workType)
            .Where(e => WorkEntitlements.Permits(actor, e.Queue.WorkType))
            .ToList();
        if (effective.Count == 0) return null;

        foreach (var tier in effective.GroupBy(e => e.IsOverflow).OrderBy(g => g.Key))
        {
            for (var round = 0; round < Math.Max(1, _options.GetNextRounds); round++)
            {
                now = Now;
                var candidates = new List<(WorkItem Item, EffectiveQueue Via)>();
                foreach (var membership in tier)
                {
                    var found = await _store.FindCandidatesAsync(
                        tenantId, membership.Queue.Id, membership.AuthorityLimit, Ms(now), _options.CandidatesPerQueue, ct);
                    candidates.AddRange(found.Select(i => (i, membership)));
                }
                if (candidates.Count == 0) break;

                foreach (var (candidate, via) in candidates
                             .OrderByDescending(c => c.Item.PriorityScore)
                             .ThenBy(c => c.Via.Rank)
                             .ThenBy(c => c.Item.CreatedAt)
                             .ThenBy(c => c.Item.Id, StringComparer.Ordinal))
                {
                    if (candidate.HasActiveLease(now)) continue; // leased since the read
                    var reclaimedFrom = candidate.Status == WorkItemStatus.Leased ? candidate.AssignedTo : null;
                    var expected = candidate.Version;
                    candidate.Status = WorkItemStatus.Leased;
                    candidate.SetLease(userId, userId, now + _options.LeaseDuration);
                    candidate.UpdatedAt = now;
                    if (!await _store.TryReplaceItemAsync(candidate, expected, ct))
                        continue; // another caller won it

                    await AuditAsync(candidate, reclaimedFrom is null ? WorkAuditActions.Leased : WorkAuditActions.Reclaimed,
                        userId, fromUser: reclaimedFrom, toUser: userId, detail: $"via queue {via.Queue.Name}");
                    return new DistributionResult(DistributionStatus.Ok, candidate);
                }
            }
        }
        return null;
    }

    /// <summary>The caller's leased items (active and run-out-but-not-reclaimed), highest score first.</summary>
    public Task<IReadOnlyList<WorkItem>> MineAsync(ICurrentActor actor, CancellationToken ct = default) =>
        _store.ListLeasedToAsync(actor.TenantId, actor.UserId, ct);

    /// <summary>The holder gives the item back to its queue.</summary>
    public Task<DistributionResult> ReleaseAsync(ICurrentActor actor, string id, CancellationToken ct = default) =>
        MutateAsync(actor.TenantId, id, ct, item =>
        {
            if (item.Status != WorkItemStatus.Leased) return (DistributionStatus.Conflict, "The item is not leased.");
            if (item.AssignedTo != actor.UserId) return (DistributionStatus.Forbidden, "Only the holder can release this item.");
            item.Status = WorkItemStatus.Open;
            item.SetLease(null, null, null);
            return (DistributionStatus.Ok, null);
        }, (item, before) => AuditAsync(item, WorkAuditActions.Released, actor.UserId, fromUser: before));

    /// <summary>
    /// The holder extends the lease by one lease duration from now. A lease
    /// that ran out can be renewed only while nobody has reclaimed it.
    /// </summary>
    public Task<DistributionResult> RenewAsync(ICurrentActor actor, string id, CancellationToken ct = default) =>
        MutateAsync(actor.TenantId, id, ct, item =>
        {
            if (item.Status != WorkItemStatus.Leased) return (DistributionStatus.Conflict, "The item is not leased.");
            if (item.AssignedTo != actor.UserId) return (DistributionStatus.Forbidden, "Only the holder can renew this lease.");
            var until = Now + _options.LeaseDuration;
            if (item.LeaseExpiresAt is { } current && current > until) until = current; // never shortens a longer assignment
            item.SetLease(item.AssignedTo, item.AssignedBy, until);
            return (DistributionStatus.Ok, null);
        }, (item, _) => AuditAsync(item, WorkAuditActions.Renewed, actor.UserId, toUser: actor.UserId));

    /// <summary>
    /// Supervisor push: the item is leased to <paramref name="userId"/> for
    /// <see cref="WorkManagementOptions.AssignedLeaseMinutes"/>, whoever held it.
    /// Push is an override: the assignee's queue membership is not required.
    /// </summary>
    public Task<DistributionResult> AssignAsync(ICurrentActor supervisor, string id, string userId, CancellationToken ct = default) =>
        MutateAsync(supervisor.TenantId, id, ct, item =>
        {
            if (!item.IsActive) return (DistributionStatus.Conflict, $"The item is {item.Status}; only open work can be assigned.");
            item.Status = WorkItemStatus.Leased;
            item.SetLease(userId, supervisor.UserId, Now + _options.AssignedLeaseDuration);
            return (DistributionStatus.Ok, null);
        }, (item, before) => AuditAsync(item,
            before is null ? WorkAuditActions.Assigned : WorkAuditActions.Reassigned,
            supervisor.UserId, fromUser: before, toUser: userId));

    /// <summary>Supervisor push: back to the queue, whoever held it.</summary>
    public Task<DistributionResult> UnassignAsync(ICurrentActor supervisor, string id, CancellationToken ct = default) =>
        MutateAsync(supervisor.TenantId, id, ct, item =>
        {
            if (!item.IsActive) return (DistributionStatus.Conflict, $"The item is {item.Status}.");
            item.Status = WorkItemStatus.Open;
            item.SetLease(null, null, null);
            return (DistributionStatus.Ok, null);
        }, (item, before) => AuditAsync(item, WorkAuditActions.Unassigned, supervisor.UserId, fromUser: before));

    /// <summary>
    /// The holder finishes person-created work. Source-owned work is never
    /// completed here: a claim's item closes when claims-service says the
    /// claim is no longer pended, and letting a person close it would leave
    /// the queue disagreeing with the claim. A follow-up from a contact has
    /// no such source, so the person who did it is the only one who can say
    /// it is done.
    /// </summary>
    public Task<DistributionResult> CompleteAsync(
        ICurrentActor actor, string id, string? resolution, CancellationToken ct = default) =>
        MutateAsync(actor.TenantId, id, ct, item =>
        {
            if (!WorkTypes.IsPersonCreated(item.WorkType))
                return (DistributionStatus.Conflict,
                    $"{item.WorkType} work is completed by its source, not from the work list.");
            if (!WorkEntitlements.Permits(actor, item.WorkType))
                return (DistributionStatus.Forbidden, $"You may not work {item.WorkType} items.");
            if (!item.IsActive) return (DistributionStatus.Conflict, $"The item is already {item.Status}.");
            if (item.Status == WorkItemStatus.Leased && item.AssignedTo != actor.UserId)
                return (DistributionStatus.Forbidden, "Only the holder can complete this item.");
            item.Status = WorkItemStatus.Done;
            item.SetLease(null, item.AssignedBy, null);
            item.CompletedAt = Now;
            item.CompletedBy = actor.UserId;
            item.Resolution = string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim();
            return (DistributionStatus.Ok, null);
        }, (item, before) => AuditAsync(item, WorkAuditActions.Completed, actor.UserId, fromUser: before,
            detail: item.Resolution));

    /// <summary>Read, check, change, compare-and-swap; retried while the item changes underneath.</summary>
    private async Task<DistributionResult> MutateAsync(
        string tenantId, string id, CancellationToken ct,
        Func<WorkItem, (DistributionStatus Status, string? Error)> change,
        Func<WorkItem, string?, Task> audit)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var item = await _store.GetItemAsync(tenantId, id, ct);
            if (item is null) return new DistributionResult(DistributionStatus.NotFound, Error: $"Work item {id} not found.");
            var expected = item.Version;
            var before = item.Status == WorkItemStatus.Leased ? item.AssignedTo : null;
            var (status, error) = change(item);
            if (status != DistributionStatus.Ok) return new DistributionResult(status, item, error);
            item.UpdatedAt = Now;
            if (await _store.TryReplaceItemAsync(item, expected, ct))
            {
                await audit(item, before);
                return new DistributionResult(DistributionStatus.Ok, item);
            }
        }
        return new DistributionResult(DistributionStatus.Conflict, Error: "The item kept changing; try again.");
    }

    private Task AuditAsync(WorkItem item, string action, string actor, string? fromUser = null, string? toUser = null, string? detail = null) =>
        _store.AppendAuditAsync(new WorkAuditEvent
        {
            TenantId = item.TenantId,
            WorkItemId = item.Id,
            Action = action,
            Actor = actor,
            FromUser = fromUser,
            ToUser = toUser,
            QueueId = item.QueueId,
            Detail = detail,
            At = Now,
        });
}
