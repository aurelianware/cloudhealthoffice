using AccumulatorService.Models;
using AccumulatorService.Repositories;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// In-memory repository for unit tests. Enforces the same invariants as the
/// real repositories — tenant partitioning, unique (tenantId, eventId), unique
/// (tenantId, aggregateId, version) — so correctness bugs surface here instead
/// of waiting for integration runs.
/// </summary>
public class InMemoryAccumulatorRepository : IAccumulatorRepository
{
    private readonly List<AccumulatorSnapshot> _snapshots = new();
    public readonly List<AccumulatorEvent> Events = new();

    public void Seed(AccumulatorSnapshot s) => _snapshots.Add(s);

    public Task<AccumulatorSnapshot?> GetSnapshotAsync(string tenantId, string memberId, DateTime planYearStart, CancellationToken ct = default)
    {
        var id = AccumulatorSnapshot.BuildId(tenantId, memberId, planYearStart);
        return Task.FromResult(_snapshots.FirstOrDefault(s => s.TenantId == tenantId && s.Id == id));
    }

    public Task<AccumulatorSnapshot?> GetSnapshotByAsOfDateAsync(string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default)
    {
        var s = _snapshots.FirstOrDefault(x =>
            x.TenantId == tenantId &&
            x.MemberId == memberId &&
            x.PlanYearStart <= asOfDate &&
            x.PlanYearEnd >= asOfDate);
        return Task.FromResult(s);
    }

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetSnapshotsAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        IReadOnlyList<AccumulatorSnapshot> result = _snapshots
            .Where(s => s.TenantId == tenantId && s.MemberId == memberId)
            .OrderByDescending(s => s.PlanYearStart)
            .ToList();
        return Task.FromResult(result);
    }

    public Task UpsertSnapshotAsync(AccumulatorSnapshot snapshot, CancellationToken ct = default)
    {
        _snapshots.RemoveAll(s => s.TenantId == snapshot.TenantId && s.Id == snapshot.Id);
        _snapshots.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
    {
        if (Events.Any(e => e.TenantId == evt.TenantId && e.EventId == evt.EventId))
            throw new InvalidOperationException($"Duplicate eventId for tenant {evt.TenantId}");
        if (Events.Any(e => e.TenantId == evt.TenantId && e.AggregateId == evt.AggregateId && e.Version == evt.Version))
            throw new InvalidOperationException($"Duplicate (aggregateId, version) for tenant {evt.TenantId}");
        Events.Add(evt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccumulatorEvent>> GetEventsAsync(string tenantId, string memberId, int take = 100, CancellationToken ct = default)
    {
        IReadOnlyList<AccumulatorEvent> result = Events
            .Where(e => e.TenantId == tenantId && e.MemberId == memberId)
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<AccumulatorEvent?> GetManualAdjustmentAsync(string tenantId, string adjustmentId, CancellationToken ct = default)
    {
        var evt = Events.FirstOrDefault(e =>
            e.TenantId == tenantId &&
            e.EventType == "ManualAdjustment" &&
            e.SourceReference == adjustmentId);
        return Task.FromResult(evt);
    }
}

public class InMemoryProcessedClaimStore : IProcessedClaimStore
{
    private readonly Dictionary<string, ProcessedClaim> _map = new();

    public Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{claimId}";
        if (_map.TryGetValue(key, out var existing))
        {
            // Pending = crashed mid-flight; allow retry.
            if (string.Equals(existing.Outcome, "Pending", StringComparison.Ordinal))
                return Task.FromResult(BeginClaimOutcome.Proceed);
            return Task.FromResult(BeginClaimOutcome.AlreadyApplied);
        }
        _map[key] = new ProcessedClaim
        {
            Id = key,
            TenantId = tenantId,
            ClaimId = claimId,
            ProcessedAt = DateTime.UtcNow,
            Outcome = "Pending"
        };
        return Task.FromResult(BeginClaimOutcome.Proceed);
    }

    public Task CompleteAsync(string tenantId, string claimId, string resultingEventId, string outcome, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{claimId}";
        if (_map.TryGetValue(key, out var p))
        {
            p.ResultingEventId = resultingEventId;
            p.Outcome = outcome;
            p.ProcessedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{claimId}";
        return Task.FromResult(_map.TryGetValue(key, out var p) ? p : null);
    }
}

public class RecordingPublisher : global::AccumulatorService.Services.IAccumulatorEventPublisher
{
    public readonly List<CloudHealthOffice.Events.AccumulatorAdjustedEvent> Adjusted = new();
    public readonly List<CloudHealthOffice.Events.OrphanAccumulatorClaimEvent> Orphans = new();

    public Task PublishAdjustedAsync(CloudHealthOffice.Events.AccumulatorAdjustedEvent evt, CancellationToken ct = default)
    {
        Adjusted.Add(evt);
        return Task.CompletedTask;
    }

    public Task PublishOrphanAsync(CloudHealthOffice.Events.OrphanAccumulatorClaimEvent evt, CancellationToken ct = default)
    {
        Orphans.Add(evt);
        return Task.CompletedTask;
    }
}
