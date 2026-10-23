using CloudHealthOffice.Infrastructure.Work;
using CloudHealthOffice.Events;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Repositories;

namespace WorkManagementService.Services;

public enum IntakeOutcome
{
    Created,
    Updated,
    Rerouted,
    Reopened,
    Completed,

    /// <summary>A duplicate or late event (its SourceVersion is not newer), or nothing changed.</summary>
    Ignored,
}

public sealed class RecomputeResult
{
    public int Examined { get; set; }
    public int Rerouted { get; set; }
    public int Rescored { get; set; }
    public int Conflicts { get; set; }
}

public sealed class ReconcileResult
{
    public string TenantId { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public int Pended { get; set; }
    public int Created { get; set; }
    public int Reopened { get; set; }
    public int Updated { get; set; }
    public int Closed { get; set; }
    public int Unchanged { get; set; }
    public DateTime StartedAt { get; set; }
}

/// <summary>
/// Turns work-source events into work items: routes and scores them, and keeps
/// them in step with their source. Idempotent and order-safe: an event whose
/// <see cref="ClaimWorkEvent.SourceVersion"/> is not newer than the item's
/// last applied one is ignored (duplicates, redeliveries, late arrivals), and
/// every write is a compare-and-swap on the item's version, retried on conflict.
/// </summary>
public sealed class WorkIntakeService
{
    private const int MaxAttempts = 8;
    public const string ReconcileActor = "system:reconcile";

    private readonly IWorkStore _store;
    private readonly WorkConfigService _config;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkIntakeService> _logger;

    public WorkIntakeService(IWorkStore store, WorkConfigService config, TimeProvider time, ILogger<WorkIntakeService> logger)
    {
        _store = store;
        _config = config;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public static WorkItemAttributes AttributesOf(ClaimWorkEvent e) => new()
    {
        PrimaryPendCode = e.PrimaryPendCode,
        PendCodes = e.PendCodes?.ToList() ?? new(),
        PendReason = e.PendReason,
        PendFingerprint = e.PendFingerprint,
        LineOfBusiness = e.LineOfBusiness,
        ClaimType = e.ClaimType,
        Amount = e.TotalChargeAmount,
        ProviderNpi = e.BillingProviderNpi,
        ProviderTin = e.BillingProviderTin,
        ProviderName = e.BillingProviderName,
        NetworkTier = e.NetworkTier,
        Expedited = e.Expedited,
        ReceivedDate = e.ReceivedDate,
        ServiceDate = e.ServiceDateFrom,
        MemberId = e.MemberId,
    };

    /// <summary>
    /// A source's report that one of its records needs work (or no longer
    /// does), whatever the work type: a claims.work.v1 event, or an
    /// enrollment exception from a reconcile.
    /// </summary>
    internal sealed record SourceChange(
        string TenantId, WorkType WorkType, string SourceId, string SourceNumber, bool Needed,
        WorkItemAttributes Attributes, long SourceVersion, string? EventId, string? ActorId,
        string? Resolution, string DefaultActor, string? CreatedDetail);

    /// <summary>Applies one claims.work.v1 event.</summary>
    public Task<IntakeOutcome> ApplyClaimEventAsync(ClaimWorkEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.TenantId) || string.IsNullOrWhiteSpace(evt.ClaimId))
            throw new ArgumentException("A claim work event needs a tenant and a claim id.", nameof(evt));
        var needed = string.Equals(evt.EventType, ClaimWorkEventTopics.WorkNeeded, StringComparison.Ordinal);
        if (!needed && !string.Equals(evt.EventType, ClaimWorkEventTopics.WorkResolved, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown claim work event type '{evt.EventType}'.", nameof(evt));
        var attributes = AttributesOf(evt);
        return ApplyAsync(new SourceChange(evt.TenantId, WorkType.Claim, evt.ClaimId, evt.ClaimNumber, needed, attributes,
            evt.SourceVersion, evt.EventId, evt.ActorId, evt.Resolution, "claims-service", $"pend {attributes.PrimaryPendCode}"), ct);
    }

    /// <summary>The work attributes of an enrollment exception: queues route on its reason code.</summary>
    public static WorkItemAttributes AttributesOf(EnrollmentWorkSnapshot s) => new()
    {
        PrimaryPendCode = s.ReasonCode,
        PendCodes = [s.ReasonCode],
        PendReason = s.ReasonCode,
        // Unchanged reason, unchanged item: a reconcile repeating the state does not rewrite it.
        PendFingerprint = s.ReasonCode,
        ReceivedDate = s.CreatedAt,
        MemberId = s.MemberId,
    };

    /// <summary>Applies one enrollment exception that needs work (from enrollment-import-service).</summary>
    public Task<IntakeOutcome> ApplyEnrollmentNeededAsync(string tenantId, EnrollmentWorkSnapshot snapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(snapshot.ExceptionId))
            throw new ArgumentException("An enrollment exception needs a tenant and an id.", nameof(snapshot));
        return ApplyAsync(new SourceChange(tenantId, WorkType.Enrollment834, snapshot.ExceptionId, snapshot.Number, Needed: true,
            AttributesOf(snapshot), snapshot.SourceVersion, $"enrollment:{snapshot.ExceptionId}:{snapshot.SourceVersion}", null, null,
            "enrollment-import-service", snapshot.ReasonCode), ct);
    }

    internal async Task<IntakeOutcome> ApplyAsync(SourceChange change, CancellationToken ct)
    {
        await _config.EnsureSeededAsync(change.TenantId, ct);
        var id = WorkItem.IdFor(change.TenantId, change.WorkType, change.SourceId);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var item = await _store.GetItemAsync(change.TenantId, id, ct);
            if (item is not null && change.SourceVersion <= item.SourceVersion)
                return IntakeOutcome.Ignored;

            var outcome = change.Needed
                ? await TryApplyNeededAsync(item, id, change, ct)
                : await TryApplyResolvedAsync(item, id, change, ct);
            if (outcome is { } done) return done;
        }

        throw new InvalidOperationException(
            $"Work item {id} kept changing under {MaxAttempts} attempts to apply {(change.Needed ? "work-needed" : "work-resolved")}; it will be retried.");
    }

    /// <summary>Null when the compare-and-swap lost (re-read and retry).</summary>
    private async Task<IntakeOutcome?> TryApplyNeededAsync(WorkItem? item, string id, SourceChange evt, CancellationToken ct)
    {
        var now = Now;
        var attributes = evt.Attributes;
        var queues = await _store.ListQueuesAsync(evt.TenantId, ct);
        var queue = WorkRouter.Route(evt.WorkType, attributes, queues);

        if (item is null)
        {
            item = new WorkItem
            {
                Id = id,
                TenantId = evt.TenantId,
                WorkType = evt.WorkType,
                SourceRef = WorkItem.SourceRefFor(evt.WorkType, evt.SourceId),
                SourceId = evt.SourceId,
                SourceNumber = evt.SourceNumber,
                Attributes = attributes,
                Status = WorkItemStatus.Open,
                CreatedAt = now,
                UpdatedAt = now,
                SourceVersion = evt.SourceVersion,
                LastEventId = evt.EventId,
            };
            Place(item, queue, now);
            if (!await _store.TryCreateItemAsync(item, ct)) return null;
            await AuditAsync(item, WorkAuditActions.Created, evt.ActorId ?? evt.DefaultActor, queueId: item.QueueId,
                detail: evt.CreatedDetail);
            return IntakeOutcome.Created;
        }

        var expected = item.Version;
        var wasActive = item.IsActive;
        var previousQueue = item.QueueId;

        // Nothing a reader would see changes: skip the write (a reconcile
        // repeating the current state must not churn versions under Get Next).
        if (wasActive && item.Attributes.PendFingerprint == attributes.PendFingerprint
            && item.Attributes.Amount == attributes.Amount && item.QueueId == (queue?.Id ?? item.QueueId))
            return IntakeOutcome.Ignored;

        item.Attributes = attributes;
        item.SourceNumber = string.IsNullOrWhiteSpace(evt.SourceNumber) ? item.SourceNumber : evt.SourceNumber;
        item.SourceVersion = evt.SourceVersion;
        item.LastEventId = evt.EventId;
        item.UpdatedAt = now;
        if (!wasActive)
        {
            // The claim needs work again (re-pended after a resolution): a fresh item in its queue.
            item.Status = WorkItemStatus.Open;
            item.SetLease(null, null, null);
            item.CompletedAt = null;
            item.CompletedBy = null;
            item.Resolution = null;
            item.CreatedAt = now;
        }
        Place(item, queue, now);
        if (!await _store.TryReplaceItemAsync(item, expected, ct)) return null;

        if (!wasActive)
        {
            await AuditAsync(item, WorkAuditActions.Reopened, evt.ActorId ?? evt.DefaultActor, queueId: item.QueueId);
            return IntakeOutcome.Reopened;
        }
        if (previousQueue != item.QueueId)
        {
            await AuditAsync(item, WorkAuditActions.Rerouted, evt.ActorId ?? evt.DefaultActor, queueId: item.QueueId,
                detail: $"from {previousQueue}");
            return IntakeOutcome.Rerouted;
        }
        return IntakeOutcome.Updated;
    }

    private async Task<IntakeOutcome?> TryApplyResolvedAsync(WorkItem? item, string id, SourceChange evt, CancellationToken ct)
    {
        var now = Now;
        if (item is null)
        {
            // Resolved before (or without) a work-needed reaching us: record a
            // completed tombstone, so a late work-needed with an older version
            // is recognised as stale instead of opening work for a resolved claim.
            var tombstone = new WorkItem
            {
                Id = id,
                TenantId = evt.TenantId,
                WorkType = evt.WorkType,
                SourceRef = WorkItem.SourceRefFor(evt.WorkType, evt.SourceId),
                SourceId = evt.SourceId,
                SourceNumber = evt.SourceNumber,
                Attributes = evt.Attributes,
                Status = WorkItemStatus.Done,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = now,
                CompletedBy = evt.ActorId,
                Resolution = evt.Resolution,
                SourceVersion = evt.SourceVersion,
                LastEventId = evt.EventId,
            };
            return await _store.TryCreateItemAsync(tombstone, ct) ? IntakeOutcome.Ignored : null;
        }

        var expected = item.Version;
        var wasActive = item.IsActive;
        var previousHolder = item.AssignedTo;
        item.SourceVersion = evt.SourceVersion;
        item.LastEventId = evt.EventId;
        item.UpdatedAt = now;
        if (wasActive)
        {
            item.Status = WorkItemStatus.Done;
            item.SetLease(null, item.AssignedBy, null);
            item.CompletedAt = now;
            item.CompletedBy = evt.ActorId;
            item.Resolution = evt.Resolution;
        }
        if (!await _store.TryReplaceItemAsync(item, expected, ct)) return null;
        if (!wasActive) return IntakeOutcome.Ignored;

        await AuditAsync(item, WorkAuditActions.Completed, evt.ActorId ?? evt.DefaultActor, fromUser: previousHolder,
            queueId: item.QueueId, detail: evt.Resolution);
        return IntakeOutcome.Completed;
    }

    /// <summary>Routes and scores an item (no write).</summary>
    internal static void Place(WorkItem item, WorkQueue? queue, DateTime now)
    {
        // No active queue for the work type (every queue deactivated): the
        // item waits unrouted, visible to supervisors, until recompute.
        var effective = queue ?? new WorkQueue { Id = string.Empty, SlaDays = 30 };
        item.QueueId = effective.Id;
        var score = PriorityScorer.Score(item.Attributes, effective, item.CreatedAt, now);
        item.PriorityScore = score.Score;
        item.PriorityReason = score.Reason;
        item.DueAt = score.DueAt;
    }

    /// <summary>
    /// Re-routes and re-scores every open item of a tenant (after a queue
    /// change, and periodically, since priority rises while an item waits).
    /// Leases are kept. An item that changed concurrently is skipped (counted
    /// as a conflict); the next run picks it up.
    /// </summary>
    public async Task<RecomputeResult> RecomputeAsync(string tenantId, bool reroute = true, CancellationToken ct = default)
    {
        await _config.EnsureSeededAsync(tenantId, ct);
        var result = new RecomputeResult();
        var queues = await _store.ListQueuesAsync(tenantId, ct);
        var byId = queues.ToDictionary(q => q.Id);
        var now = Now;
        // Every work type, not only Claim: priority rises while an item
        // waits whatever produced it, and an enrollment or follow-up queue
        // that an admin re-criteria'd has to re-route like a claim queue.
        var active = new List<WorkItem>();
        foreach (var type in Enum.GetValues<WorkType>())
            active.AddRange(await _store.ListActiveItemsAsync(tenantId, type, ct));
        foreach (var item in active)
        {
            result.Examined++;
            var expected = item.Version;
            var previousQueue = item.QueueId;
            var previousScore = item.PriorityScore;
            var queue = reroute
                ? WorkRouter.Route(item.WorkType, item.Attributes, queues)
                : byId.GetValueOrDefault(item.QueueId);
            Place(item, queue, now);
            var rerouted = previousQueue != item.QueueId;
            if (!rerouted && Math.Abs(previousScore - item.PriorityScore) < 0.005) continue;
            item.UpdatedAt = now;
            if (!await _store.TryReplaceItemAsync(item, expected, ct))
            {
                result.Conflicts++;
                continue;
            }
            if (rerouted)
            {
                result.Rerouted++;
                await AuditAsync(item, WorkAuditActions.Rerouted, "system:recompute", queueId: item.QueueId, detail: $"from {previousQueue}");
            }
            else
            {
                result.Rescored++;
            }
        }
        return result;
    }

    /// <summary>
    /// Repairs drift against claims-service: every pended claim gets an open
    /// item (created, reopened, or updated if its pends changed), and every
    /// open item whose claim is no longer pended is closed. Closing happens
    /// only after the whole pended set was read: a failed page aborts the run
    /// before anything is closed.
    /// </summary>
    public async Task<ReconcileResult> ReconcileClaimsAsync(string tenantId, IClaimsWorkSource source, int pageSize, CancellationToken ct = default)
    {
        var result = new ReconcileResult { TenantId = tenantId, StartedAt = Now };
        var pended = new List<ClaimWorkEvent>();
        try
        {
            for (var page = 1; ; page++)
            {
                var snapshot = await source.GetPendedPageAsync(tenantId, page, pageSize, ct);
                pended.AddRange(snapshot.Items);
                if (snapshot.Items.Count == 0 || page * snapshot.PageSize >= snapshot.TotalCount) break;
                if (page >= 10_000) throw new InvalidOperationException("Snapshot paging did not terminate.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reconcile for tenant {TenantId} could not read claims-service; nothing changed", tenantId);
            result.Error = $"claims-service unavailable: {ex.Message}";
            return result;
        }

        result.Pended = pended.Count;
        var pendedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in pended)
        {
            snapshot.TenantId = tenantId; // the token's tenant; never trust a payload's
            pendedIds.Add(snapshot.ClaimId);
            switch (await ApplyClaimEventAsync(snapshot, ct))
            {
                case IntakeOutcome.Created: result.Created++; break;
                case IntakeOutcome.Reopened: result.Reopened++; break;
                case IntakeOutcome.Updated or IntakeOutcome.Rerouted: result.Updated++; break;
                default: result.Unchanged++; break;
            }
        }

        var closeVersion = result.StartedAt.Ticks;
        foreach (var item in await _store.ListActiveItemsAsync(tenantId, WorkType.Claim, ct))
        {
            if (pendedIds.Contains(item.SourceId) || item.SourceVersion >= closeVersion) continue;
            var outcome = await ApplyClaimEventAsync(new ClaimWorkEvent
            {
                EventId = $"reconcile:{item.SourceId}:{closeVersion}",
                EventType = ClaimWorkEventTopics.WorkResolved,
                TenantId = tenantId,
                ClaimId = item.SourceId,
                ClaimNumber = item.SourceNumber,
                SourceVersion = closeVersion,
                Resolution = "No longer pended (reconcile)",
                ActorId = ReconcileActor,
            }, ct);
            if (outcome == IntakeOutcome.Completed) result.Closed++;
        }

        result.Succeeded = true;
        _logger.LogInformation(
            "Reconciled tenant {TenantId}: {Pended} pended, {Created} created, {Reopened} reopened, {Updated} updated, {Closed} closed",
            tenantId, result.Pended, result.Created, result.Reopened, result.Updated, result.Closed);
        return result;
    }

    /// <summary>
    /// Brings Enrollment834 items in step with enrollment-import-service's
    /// exceptions that need work: each becomes (or stays) an item, and every
    /// open item whose exception no longer needs work (resolved, or on hold) is
    /// closed. Like the claims reconcile, nothing closes unless every page was
    /// read. Enrollment has no event stream yet, so this is how its work flows.
    /// </summary>
    public async Task<ReconcileResult> ReconcileEnrollmentAsync(string tenantId, IEnrollmentWorkSource source, int pageSize, CancellationToken ct = default)
    {
        var result = new ReconcileResult { TenantId = tenantId, StartedAt = Now };
        var needed = new List<EnrollmentWorkSnapshot>();
        try
        {
            for (var page = 1; ; page++)
            {
                var snapshot = await source.GetWorkablePageAsync(tenantId, page, pageSize, ct);
                needed.AddRange(snapshot.Items);
                if (snapshot.Items.Count == 0 || page * snapshot.PageSize >= snapshot.TotalCount) break;
                if (page >= 10_000) throw new InvalidOperationException("Snapshot paging did not terminate.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Enrollment reconcile for tenant {TenantId} could not read enrollment-import-service; nothing changed", tenantId);
            result.Error = $"enrollment-import-service unavailable: {ex.Message}";
            return result;
        }

        result.Pended = needed.Count;
        var neededIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in needed)
        {
            neededIds.Add(snapshot.ExceptionId);
            switch (await ApplyEnrollmentNeededAsync(tenantId, snapshot, ct))
            {
                case IntakeOutcome.Created: result.Created++; break;
                case IntakeOutcome.Reopened: result.Reopened++; break;
                case IntakeOutcome.Updated or IntakeOutcome.Rerouted: result.Updated++; break;
                default: result.Unchanged++; break;
            }
        }

        var closeVersion = result.StartedAt.Ticks;
        foreach (var item in await _store.ListActiveItemsAsync(tenantId, WorkType.Enrollment834, ct))
        {
            if (neededIds.Contains(item.SourceId) || item.SourceVersion >= closeVersion) continue;
            var outcome = await ApplyAsync(new SourceChange(tenantId, WorkType.Enrollment834, item.SourceId, item.SourceNumber,
                Needed: false, item.Attributes, closeVersion, $"reconcile:{item.SourceId}:{closeVersion}", ReconcileActor,
                "Resolved or on hold in enrollment (reconcile)", ReconcileActor, null), ct);
            if (outcome == IntakeOutcome.Completed) result.Closed++;
        }

        result.Succeeded = true;
        _logger.LogInformation(
            "Reconciled enrollment work for tenant {TenantId}: {Needed} needing work, {Created} created, {Reopened} reopened, {Updated} updated, {Closed} closed",
            tenantId, result.Pended, result.Created, result.Reopened, result.Updated, result.Closed);
        return result;
    }

    /// <summary>What a person asked to be followed up, and where it opens.</summary>
    public sealed record AdHocWork(
        string TenantId, WorkType WorkType, string SourceNumber, string Title, string? OpenUrl,
        IReadOnlyList<string> ReasonCodes, IReadOnlyDictionary<string, List<string>> Facets,
        string? MemberId, string? ProviderNpi, string? LineOfBusiness, bool Expedited,
        DateTime? Deadline, string Actor);

    /// <summary>
    /// Creates one item of work a person asked for, with no source service
    /// behind it: a follow-up from a contact. It is routed and scored like
    /// any other item, and only a person closes it — no reconcile owns it, so
    /// nothing will cancel it out from under the queue.
    ///
    /// <para>Its source id is generated here rather than taken from the
    /// caller: two follow-ups from one contact are two pieces of work, and a
    /// caller-chosen id would let one overwrite the other.</para>
    /// </summary>
    public async Task<WorkItem> CreateAdHocAsync(AdHocWork request, CancellationToken ct = default)
    {
        if (!WorkTypes.IsPersonCreated(request.WorkType))
            throw new ArgumentException(
                $"{request.WorkType} work comes from its source service, not from a person.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TenantId))
            throw new ArgumentException("A work item needs a tenant.", nameof(request));

        await _config.EnsureSeededAsync(request.TenantId, ct);
        var now = Now;
        var sourceId = Guid.NewGuid().ToString("N");
        var reasons = request.ReasonCodes
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var attributes = new WorkItemAttributes
        {
            PrimaryPendCode = reasons.FirstOrDefault(),
            PendCodes = reasons,
            PendReason = request.Title,
            Title = request.Title,
            OpenUrl = request.OpenUrl,
            LineOfBusiness = request.LineOfBusiness,
            MemberId = request.MemberId,
            ProviderNpi = request.ProviderNpi,
            Expedited = request.Expedited,
            Deadline = request.Deadline,
            ReceivedDate = now,
            Facets = request.Facets
                .Select(f => (Key: WorkFacets.Key(f.Key), Values: f.Value
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .Where(f => WorkFacets.IsValidKey(f.Key) && f.Values.Count > 0)
                .ToDictionary(f => f.Key, f => f.Values),
        };

        var item = new WorkItem
        {
            Id = WorkItem.IdFor(request.TenantId, request.WorkType, sourceId),
            TenantId = request.TenantId,
            WorkType = request.WorkType,
            SourceRef = WorkItem.SourceRefFor(request.WorkType, sourceId),
            SourceId = sourceId,
            SourceNumber = request.SourceNumber,
            Attributes = attributes,
            Status = WorkItemStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
            // No source version to be in step with: a person is the source,
            // and there is no later event that could be judged stale.
            SourceVersion = now.Ticks,
        };
        Place(item, WorkRouter.Route(request.WorkType, attributes, await _store.ListQueuesAsync(request.TenantId, ct)), now);

        if (!await _store.TryCreateItemAsync(item, ct))
            throw new InvalidOperationException($"Work item {item.Id} already exists.");
        await AuditAsync(item, WorkAuditActions.Created, request.Actor, queueId: item.QueueId, detail: request.Title);
        return item;
    }

    private Task AuditAsync(WorkItem item, string action, string actor, string? fromUser = null, string? toUser = null,
        string? queueId = null, string? detail = null) =>
        _store.AppendAuditAsync(new WorkAuditEvent
        {
            TenantId = item.TenantId,
            WorkItemId = item.Id,
            Action = action,
            Actor = actor,
            FromUser = fromUser,
            ToUser = toUser,
            QueueId = queueId,
            Detail = detail,
            At = Now,
        });
}
