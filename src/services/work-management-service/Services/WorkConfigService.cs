using System.Collections.Concurrent;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Repositories;

namespace WorkManagementService.Services;

/// <summary>Tenants this process has already seeded (singleton): seeding is checked once per tenant per process.</summary>
public sealed class WorkSeedTracker
{
    private readonly ConcurrentDictionary<string, bool> _tenants = new(StringComparer.Ordinal);

    public bool IsSeeded(string tenantId) => _tenants.ContainsKey(tenantId);
    public void MarkSeeded(string tenantId) => _tenants[tenantId] = true;
    public void Clear() => _tenants.Clear();
}

/// <summary>Queue fields an admin edits.</summary>
public sealed class QueueInput
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public WorkType WorkType { get; set; } = WorkType.Claim;
    public bool IsActive { get; set; } = true;
    public int? Order { get; set; }
    public bool IsFallback { get; set; }
    public WorkQueueCriteria Criteria { get; set; } = new();
    public PriorityWeights Weights { get; set; } = new();
    public int SlaDays { get; set; } = 30;
}

public sealed class TeamInput
{
    public string Name { get; set; } = string.Empty;
    public List<string> UserIds { get; set; } = new();
}

public sealed class MembershipInput
{
    public string? UserId { get; set; }
    public string? TeamId { get; set; }
    public string QueueId { get; set; } = string.Empty;
    public int Rank { get; set; } = 1;
    public bool IsOverflow { get; set; }
    public decimal? AuthorityLimit { get; set; }
}

/// <summary>One queue a user pulls from, after merging direct and team memberships.</summary>
public sealed record EffectiveQueue(WorkQueue Queue, int Rank, bool IsOverflow, decimal? AuthorityLimit, IReadOnlyList<string> Via);

/// <summary>A result or the reason it was refused (400 / 404 / 409).</summary>
public sealed record ConfigResult<T>(T? Value, int Status = 200, string? Error = null)
{
    public static ConfigResult<T> Ok(T value) => new(value);
    public static ConfigResult<T> Bad(string error) => new(default, 400, error);
    public static ConfigResult<T> NotFound(string error) => new(default, 404, error);
    public static ConfigResult<T> Conflict(string error) => new(default, 409, error);
}

/// <summary>
/// Queue, team and membership configuration, the per-tenant default queues,
/// and effective-queue resolution (user → ordered queues).
/// </summary>
public sealed class WorkConfigService
{
    private readonly IWorkStore _store;
    private readonly WorkSeedTracker _seeded;
    private readonly IWorkEntitlementDirectory _entitlements;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkConfigService> _logger;

    private readonly IReadOnlyList<string> _seedMembers;

    public WorkConfigService(
        IWorkStore store, WorkSeedTracker seeded, IWorkEntitlementDirectory entitlements,
        TimeProvider time, ILogger<WorkConfigService> logger,
        Microsoft.Extensions.Options.IOptions<WorkManagementOptions>? options = null)
    {
        _store = store;
        _seeded = seeded;
        _entitlements = entitlements;
        _time = time;
        _logger = logger;
        _seedMembers = (options?.Value.SeedMemberUserIds ?? new List<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── Default queues ───────────────────────────────────────────────────

    /// <summary>
    /// Seeds the default claim queues the first time a tenant is used: only
    /// when it has no claim queue at all, and with deterministic ids, so
    /// concurrent first uses (two replicas) create each queue once.
    /// </summary>
    public async Task EnsureSeededAsync(string tenantId, CancellationToken ct = default)
    {
        if (_seeded.IsSeeded(tenantId)) return;

        var queues = await _store.ListQueuesAsync(tenantId, ct);
        if (!queues.Any(q => q.WorkType == WorkType.Claim))
        {
            var created = 0;
            foreach (var definition in DefaultClaimQueues.All)
            {
                if (await _store.CreateQueueIfAbsentAsync(DefaultClaimQueues.Create(definition, tenantId, Now), ct))
                    created++;
            }
            if (created > 0)
                _logger.LogInformation("Seeded {Count} default claim queues for tenant {TenantId}", created, tenantId);
        }
        if (!queues.Any(q => q.WorkType == WorkType.Enrollment834))
        {
            var created = 0;
            foreach (var definition in DefaultEnrollmentQueues.All)
            {
                if (await _store.CreateQueueIfAbsentAsync(DefaultEnrollmentQueues.Create(definition, tenantId, Now), ct))
                    created++;
            }
            if (created > 0)
                _logger.LogInformation("Seeded {Count} default enrollment queues for tenant {TenantId}", created, tenantId);
        }
        if (!queues.Any(q => q.WorkType == WorkType.Inquiry))
        {
            var created = 0;
            foreach (var definition in DefaultInquiryQueues.All)
            {
                if (await _store.CreateQueueIfAbsentAsync(DefaultInquiryQueues.Create(definition, tenantId, Now), ct))
                    created++;
            }
            if (created > 0)
                _logger.LogInformation("Seeded {Count} default follow-up queues for tenant {TenantId}", created, tenantId);
        }
        if (_seedMembers.Count > 0)
            await SeedMembershipsAsync(tenantId, ct);
        _seeded.MarkSeeded(tenantId);
    }

    /// <summary>
    /// Gives each configured seed user a primary membership in every active
    /// claim queue (enrollment queues wait for the Enrollment Ops exception
    /// screen, so Get Next never opens a page that does not exist yet), unless
    /// they already have any membership in the tenant (so
    /// an admin's own setup is never overwritten). Deterministic ids keep it
    /// idempotent across replicas and restarts.
    /// </summary>
    private async Task SeedMembershipsAsync(string tenantId, CancellationToken ct)
    {
        var memberships = await _store.ListMembershipsAsync(tenantId, ct);
        var queues = WorkRouter.Ordered(await _store.ListQueuesAsync(tenantId, ct))
            .Where(q => q.WorkType == WorkType.Claim && q.IsActive)
            .ToList();
        foreach (var userId in _seedMembers)
        {
            if (memberships.Any(m => string.Equals(m.UserId, userId, StringComparison.Ordinal))) continue;
            foreach (var queue in queues)
            {
                await _store.UpsertMembershipAsync(new WorkMembership
                {
                    Id = $"seed-{userId}-{queue.Id}",
                    TenantId = tenantId,
                    UserId = userId,
                    QueueId = queue.Id,
                    Rank = 1,
                    UpdatedAt = Now,
                    UpdatedBy = "seed",
                }, ct);
            }
            _logger.LogInformation("Gave seed user {UserId} the {Count} claim queues of tenant {TenantId}",
                userId, queues.Count, tenantId);
        }
    }

    public async Task<IReadOnlyList<WorkQueue>> ListQueuesAsync(string tenantId, CancellationToken ct = default)
    {
        await EnsureSeededAsync(tenantId, ct);
        return WorkRouter.Ordered(await _store.ListQueuesAsync(tenantId, ct)).ToList();
    }

    // ── Queues ───────────────────────────────────────────────────────────

    public async Task<ConfigResult<WorkQueue>> CreateQueueAsync(string tenantId, QueueInput input, string actor, CancellationToken ct = default)
    {
        var queues = (await ListQueuesAsync(tenantId, ct)).ToList();
        var queue = new WorkQueue
        {
            TenantId = tenantId,
            CreatedAt = Now,
            Order = input.Order ?? (queues.Where(q => q.WorkType == input.WorkType && !q.IsFallback)
                .Select(q => q.Order).DefaultIfEmpty(0).Max() + 10),
        };
        Apply(queue, input, actor);
        if (Validate(queue, queues.Append(queue).ToList()) is { } error)
            return ConfigResult<WorkQueue>.Bad(error);
        await _store.UpsertQueueAsync(queue, ct);
        return ConfigResult<WorkQueue>.Ok(queue);
    }

    public async Task<ConfigResult<WorkQueue>> UpdateQueueAsync(string tenantId, string id, QueueInput input, string actor, CancellationToken ct = default)
    {
        var queues = (await ListQueuesAsync(tenantId, ct)).ToList();
        var queue = queues.FirstOrDefault(q => q.Id == id);
        if (queue is null) return ConfigResult<WorkQueue>.NotFound($"Queue {id} not found.");
        if (input.WorkType != queue.WorkType)
            return ConfigResult<WorkQueue>.Bad("A queue's work type cannot change; create a new queue.");
        Apply(queue, input, actor);
        if (input.Order is { } order) queue.Order = order;
        if (Validate(queue, queues) is { } error)
            return ConfigResult<WorkQueue>.Bad(error);
        await _store.UpsertQueueAsync(queue, ct);
        return ConfigResult<WorkQueue>.Ok(queue);
    }

    public async Task<ConfigResult<bool>> DeleteQueueAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var queues = (await ListQueuesAsync(tenantId, ct)).ToList();
        var queue = queues.FirstOrDefault(q => q.Id == id);
        if (queue is null) return ConfigResult<bool>.NotFound($"Queue {id} not found.");
        var counts = await _store.CountByQueueAsync(tenantId, ct);
        if (counts.FirstOrDefault(c => c.QueueId == id) is { } c && c.Open + c.Leased > 0)
            return ConfigResult<bool>.Conflict(
                $"Queue '{queue.Name}' holds {c.Open + c.Leased} open item(s). Deactivate it and run recompute to move them, then delete it.");
        var remaining = queues.Where(q => q.Id != id).ToList();
        if (FallbackError(queue.WorkType, remaining) is { } error)
            return ConfigResult<bool>.Conflict(error);
        await _store.DeleteQueueAsync(tenantId, id, ct);
        foreach (var membership in (await _store.ListMembershipsAsync(tenantId, ct)).Where(m => m.QueueId == id))
            await _store.DeleteMembershipAsync(tenantId, membership.Id, ct);
        return ConfigResult<bool>.Ok(true);
    }

    /// <summary>Sets routing order to the given sequence (10, 20, 30 ...). Queues not named keep their place after them.</summary>
    public async Task<ConfigResult<IReadOnlyList<WorkQueue>>> ReorderAsync(
        string tenantId, WorkType type, IReadOnlyList<string> orderedIds, string actor, CancellationToken ct = default)
    {
        var queues = (await ListQueuesAsync(tenantId, ct)).Where(q => q.WorkType == type && !q.IsFallback).ToList();
        if (orderedIds.Distinct().Count() != orderedIds.Count)
            return ConfigResult<IReadOnlyList<WorkQueue>>.Bad("A queue is listed twice.");
        if (orderedIds.FirstOrDefault(id => queues.All(q => q.Id != id)) is { } unknown)
            return ConfigResult<IReadOnlyList<WorkQueue>>.Bad($"Queue {unknown} is not a non-fallback {type} queue of this tenant.");
        var sequence = orderedIds.Select(id => queues.Single(q => q.Id == id))
            .Concat(WorkRouter.Ordered(queues.Where(q => !orderedIds.Contains(q.Id))))
            .ToList();
        for (var i = 0; i < sequence.Count; i++)
        {
            sequence[i].Order = (i + 1) * 10;
            sequence[i].UpdatedAt = Now;
            sequence[i].UpdatedBy = actor;
            await _store.UpsertQueueAsync(sequence[i], ct);
        }
        return ConfigResult<IReadOnlyList<WorkQueue>>.Ok(sequence);
    }

    private void Apply(WorkQueue queue, QueueInput input, string actor)
    {
        queue.Name = input.Name?.Trim() ?? string.Empty;
        queue.Description = input.Description;
        queue.WorkType = input.WorkType;
        queue.IsActive = input.IsActive;
        queue.IsFallback = input.IsFallback;
        queue.Criteria = Normalize(input.Criteria ?? new WorkQueueCriteria());
        queue.Weights = input.Weights ?? new PriorityWeights();
        queue.SlaDays = input.SlaDays;
        queue.UpdatedAt = Now;
        queue.UpdatedBy = actor;
    }

    private static WorkQueueCriteria Normalize(WorkQueueCriteria c)
    {
        static List<string> Clean(List<string>? values, bool upper = false) =>
            (values ?? new()).Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => upper ? v.Trim().ToUpperInvariant() : v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new WorkQueueCriteria
        {
            // A facet whose values all cleaned away is dropped rather than
            // kept empty, so "every facet named here must hold" stays true of
            // what is stored.
            Facets = (c.Facets ?? new()).GroupBy(f => WorkFacets.Key(f.Key))
                .Select(g => (Key: g.Key, Values: Clean(g.SelectMany(x => x.Value ?? new()).ToList())))
                .Where(f => f.Values.Count > 0)
                .ToDictionary(f => f.Key, f => f.Values),
            PendCodes = Clean(c.PendCodes, upper: true),
            MatchAnyPendCode = c.MatchAnyPendCode,
            LinesOfBusiness = Clean(c.LinesOfBusiness),
            ClaimTypes = Clean(c.ClaimTypes),
            MinAmount = c.MinAmount,
            MaxAmount = c.MaxAmount,
            ProviderNpis = Clean(c.ProviderNpis),
            ProviderTins = Clean(c.ProviderTins),
            NetworkTiers = Clean(c.NetworkTiers),
            Expedited = c.Expedited,
        };
    }

    internal static string? Validate(WorkQueue queue, IReadOnlyList<WorkQueue> all)
    {
        if (string.IsNullOrWhiteSpace(queue.Name)) return "Name is required.";
        if (queue.Name.Length > 100) return "Name is at most 100 characters.";
        if (all.Any(q => q.Id != queue.Id && q.WorkType == queue.WorkType
                         && string.Equals(q.Name, queue.Name, StringComparison.OrdinalIgnoreCase)))
            return $"A {queue.WorkType} queue named '{queue.Name}' already exists.";
        if (queue.SlaDays is < 1 or > 365) return "SlaDays must be between 1 and 365.";
        if (queue.Order is < 0 or > 100_000) return "Order must be between 0 and 100000.";
        var w = queue.Weights;
        if (new[] { w.Deadline, w.Age, w.Amount, w.Expedited }.Any(x => double.IsNaN(x) || x < 0 || x > 10))
            return "Priority weights must be between 0 and 10.";
        var c = queue.Criteria;
        if (c.MinAmount is < 0 || c.MaxAmount is < 0) return "Amounts cannot be negative.";
        if (c.MinAmount is { } min && c.MaxAmount is { } max && min > max) return "MinAmount cannot exceed MaxAmount.";
        if (c.Facets.Count > 10) return "A queue routes on at most 10 facets.";
        foreach (var (name, values) in c.Facets)
        {
            if (!WorkFacets.IsValidKey(name))
                return $"Facet name '{name}' must be 1 to 40 characters of a-z, 0-9 and underscore.";
            if (values.Count > 50) return $"Facet '{name}' lists at most 50 values.";
            if (values.Any(v => v.Length > 100)) return $"Facet '{name}' values are at most 100 characters.";
        }
        return FallbackError(queue.WorkType, all);
    }

    /// <summary>A work type with active queues has exactly one active fallback.</summary>
    private static string? FallbackError(WorkType type, IEnumerable<WorkQueue> all)
    {
        var active = all.Where(q => q.WorkType == type && q.IsActive).ToList();
        var fallbacks = active.Count(q => q.IsFallback);
        if (fallbacks > 1) return $"{type} already has an active fallback queue; there can be only one.";
        if (active.Count > 0 && fallbacks == 0)
            return $"{type} needs an active fallback queue (it catches every item no other queue matches).";
        return null;
    }

    // ── Teams ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<WorkTeam>> ListTeamsAsync(string tenantId, CancellationToken ct = default) =>
        _store.ListTeamsAsync(tenantId, ct);

    public async Task<ConfigResult<WorkTeam>> SaveTeamAsync(string tenantId, string? id, TeamInput input, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return ConfigResult<WorkTeam>.Bad("Name is required.");
        WorkTeam team;
        if (id is null)
        {
            team = new WorkTeam { TenantId = tenantId };
        }
        else
        {
            var existing = await _store.GetTeamAsync(tenantId, id, ct);
            if (existing is null) return ConfigResult<WorkTeam>.NotFound($"Team {id} not found.");
            team = existing;
        }
        team.Name = input.Name.Trim();
        team.UserIds = (input.UserIds ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim())
            .Distinct(StringComparer.Ordinal).ToList();
        team.UpdatedAt = Now;
        team.UpdatedBy = actor;
        await _store.UpsertTeamAsync(team, ct);
        return ConfigResult<WorkTeam>.Ok(team);
    }

    public async Task<ConfigResult<bool>> DeleteTeamAsync(string tenantId, string id, CancellationToken ct = default)
    {
        if (!await _store.DeleteTeamAsync(tenantId, id, ct)) return ConfigResult<bool>.NotFound($"Team {id} not found.");
        foreach (var membership in (await _store.ListMembershipsAsync(tenantId, ct)).Where(m => m.TeamId == id))
            await _store.DeleteMembershipAsync(tenantId, membership.Id, ct);
        return ConfigResult<bool>.Ok(true);
    }

    // ── Memberships ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<WorkMembership>> ListMembershipsAsync(string tenantId, CancellationToken ct = default) =>
        _store.ListMembershipsAsync(tenantId, ct);

    public async Task<ConfigResult<WorkMembership>> SaveMembershipAsync(
        string tenantId, string? id, MembershipInput input, string actor, CancellationToken ct = default)
    {
        var hasUser = !string.IsNullOrWhiteSpace(input.UserId);
        var hasTeam = !string.IsNullOrWhiteSpace(input.TeamId);
        if (hasUser == hasTeam) return ConfigResult<WorkMembership>.Bad("Set exactly one of userId or teamId.");
        if (input.Rank < 1 || input.Rank > 1000) return ConfigResult<WorkMembership>.Bad("Rank must be between 1 and 1000.");
        if (input.AuthorityLimit is < 0) return ConfigResult<WorkMembership>.Bad("AuthorityLimit cannot be negative.");
        await EnsureSeededAsync(tenantId, ct);
        if (await _store.GetQueueAsync(tenantId, input.QueueId, ct) is null)
            return ConfigResult<WorkMembership>.Bad($"Queue {input.QueueId} not found.");
        if (hasTeam && await _store.GetTeamAsync(tenantId, input.TeamId!, ct) is null)
            return ConfigResult<WorkMembership>.Bad($"Team {input.TeamId} not found.");

        var queue = (await _store.GetQueueAsync(tenantId, input.QueueId, ct))!;
        if (await RefuseUnentitledAsync(tenantId, input, queue.WorkType, ct) is { } refusal)
            return refusal;

        var all = await _store.ListMembershipsAsync(tenantId, ct);
        WorkMembership membership;
        if (id is null)
        {
            membership = new WorkMembership { TenantId = tenantId };
        }
        else
        {
            membership = all.FirstOrDefault(m => m.Id == id)!;
            if (membership is null) return ConfigResult<WorkMembership>.NotFound($"Membership {id} not found.");
        }
        var userId = hasUser ? input.UserId!.Trim() : null;
        var teamId = hasTeam ? input.TeamId!.Trim() : null;
        if (all.Any(m => m.Id != membership.Id && m.QueueId == input.QueueId && m.UserId == userId && m.TeamId == teamId))
            return ConfigResult<WorkMembership>.Conflict("That member already belongs to this queue; edit the existing membership.");

        membership.UserId = userId;
        membership.TeamId = teamId;
        membership.QueueId = input.QueueId;
        membership.Rank = input.Rank;
        membership.IsOverflow = input.IsOverflow;
        membership.AuthorityLimit = input.AuthorityLimit;
        membership.UpdatedAt = Now;
        membership.UpdatedBy = actor;
        await _store.UpsertMembershipAsync(membership, ct);
        return ConfigResult<WorkMembership>.Ok(membership);
    }

    public async Task<ConfigResult<bool>> DeleteMembershipAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _store.DeleteMembershipAsync(tenantId, id, ct)
            ? ConfigResult<bool>.Ok(true)
            : ConfigResult<bool>.NotFound($"Membership {id} not found.");

    /// <summary>
    /// A membership says a person pulls this queue's work, so everybody it
    /// names must be entitled to that work type. Without this, a claim queue
    /// could hold a member who can never resolve a claim: Get Next would skip
    /// them (it filters on entitlement), the queue's staffing would read as
    /// larger than it is, and a supervisor would see them in the picker.
    ///
    /// <para>Refused both when the answer is no and when there is no answer,
    /// for the reason given on
    /// <see cref="WorkDistributionService.AssignAsync"/>: an unchecked
    /// membership is a staffing number nobody can trust.</para>
    /// </summary>
    private async Task<ConfigResult<WorkMembership>?> RefuseUnentitledAsync(
        string tenantId, MembershipInput input, WorkType workType, CancellationToken ct)
    {
        var permission = WorkEntitlements.PermissionFor(workType);

        var users = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.UserId))
        {
            users.Add(input.UserId.Trim());
        }
        else if (await _store.GetTeamAsync(tenantId, input.TeamId!.Trim(), ct) is { } team)
        {
            users.AddRange(team.UserIds.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct(StringComparer.Ordinal));
        }

        var denied = new List<string>();
        var unknown = new List<string>();
        foreach (var user in users)
        {
            switch (await _entitlements.CheckAsync(tenantId, user, permission, ct))
            {
                case Entitlement.Denied: denied.Add(user); break;
                case Entitlement.Unknown: unknown.Add(user); break;
            }
        }

        if (unknown.Count > 0)
            return ConfigResult<WorkMembership>.Conflict(
                $"Could not establish whether {Name(unknown)} may work {workType} items ({permission}): " +
                "tenant-service did not answer. The membership was not saved rather than saved unchecked; try again shortly.");

        if (denied.Count > 0)
            return ConfigResult<WorkMembership>.Bad(
                $"{Name(denied)} may not work {workType} items ({permission}), so this queue would list somebody " +
                "who can never pull from it. Give them a role that grants it, or leave them out of this queue.");

        return null;

        // Up to five names, so a large team's refusal stays readable.
        static string Name(List<string> users) => users.Count <= 5
            ? string.Join(", ", users)
            : string.Join(", ", users.Take(5)) + $" and {users.Count - 5} others";
    }

    /// <summary>
    /// The queues <paramref name="userId"/> pulls from: direct memberships and
    /// those of the user's teams, on active queues. A queue reached twice keeps
    /// the strongest terms: primary over overflow, the lowest rank, the highest
    /// authority limit (no limit beats any limit). Primary tier first, then by rank.
    /// </summary>
    public async Task<IReadOnlyList<EffectiveQueue>> EffectiveQueuesAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        var queues = (await ListQueuesAsync(tenantId, ct)).Where(q => q.IsActive).ToDictionary(q => q.Id);
        var teams = (await _store.ListTeamsAsync(tenantId, ct)).Where(t => t.UserIds.Contains(userId)).ToDictionary(t => t.Id);
        var memberships = (await _store.ListMembershipsAsync(tenantId, ct))
            .Where(m => m.UserId == userId || (m.TeamId is not null && teams.ContainsKey(m.TeamId)))
            .Where(m => queues.ContainsKey(m.QueueId));

        return memberships
            .GroupBy(m => m.QueueId)
            .Select(g => new EffectiveQueue(
                queues[g.Key],
                g.Min(m => m.Rank),
                g.All(m => m.IsOverflow),
                g.Any(m => m.AuthorityLimit is null) ? null : g.Max(m => m.AuthorityLimit),
                g.Select(m => m.UserId is not null ? "direct" : $"team:{teams[m.TeamId!].Name}").Distinct().ToList()))
            .OrderBy(e => e.IsOverflow).ThenBy(e => e.Rank).ThenBy(e => e.Queue.Order)
            .ToList();
    }

    /// <summary>Every user id a membership names (directly or through a team): who work can be pushed to.</summary>
    public async Task<IReadOnlyList<string>> AssigneesAsync(string tenantId, CancellationToken ct = default)
    {
        var teams = (await _store.ListTeamsAsync(tenantId, ct)).ToDictionary(t => t.Id);
        return (await _store.ListMembershipsAsync(tenantId, ct))
            .SelectMany(m => m.UserId is not null
                ? new[] { m.UserId }
                : m.TeamId is not null && teams.TryGetValue(m.TeamId, out var t) ? t.UserIds.ToArray() : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();
    }
}
