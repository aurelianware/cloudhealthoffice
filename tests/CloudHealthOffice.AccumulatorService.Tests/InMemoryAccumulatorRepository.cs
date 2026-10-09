using System.Text.Json;
using AccumulatorService.Models;
using AccumulatorService.Repositories;

namespace CloudHealthOffice.AccumulatorService.Tests;

/// <summary>
/// In-memory repository for unit tests. Enforces the same invariants as the
/// real repositories — tenant partitioning, unique (tenantId, eventId), unique
/// event id, unique (tenantId, aggregateId, version), one ClaimReversed row
/// per (tenantId, sourceClaimId), version-conditional snapshot writes — and
/// hands out copies (as a database would), so correctness bugs surface here
/// instead of waiting for integration runs.
/// </summary>
public class InMemoryAccumulatorRepository : IAccumulatorRepository
{
    private readonly List<AccumulatorSnapshot> _snapshots = new();
    public readonly List<AccumulatorEvent> Events = new();

    /// <summary>Runs once before the next event append (to simulate a concurrent writer).</summary>
    public Func<AccumulatorEvent, Task>? BeforeNextAppend { get; set; }

    /// <summary>When set, the next snapshot write throws (a crash after the event append).</summary>
    public bool ThrowOnNextSnapshotWrite { get; set; }

    public void Seed(AccumulatorSnapshot s) => _snapshots.Add(Copy(s));

    /// <summary>Writes a snapshot as-is (test setup, e.g. a legacy state), bypassing the version check.</summary>
    public void Overwrite(AccumulatorSnapshot s)
    {
        _snapshots.RemoveAll(x => x.TenantId == s.TenantId && x.Id == s.Id);
        _snapshots.Add(Copy(s));
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public Task<AccumulatorSnapshot?> GetSnapshotAsync(string tenantId, string memberId, DateTime planYearStart, CancellationToken ct = default)
    {
        var id = AccumulatorSnapshot.BuildId(tenantId, memberId, planYearStart);
        var s = _snapshots.FirstOrDefault(s => s.TenantId == tenantId && s.Id == id);
        return Task.FromResult(s is null ? null : Copy(s));
    }

    public Task<AccumulatorSnapshot?> GetSnapshotByAsOfDateAsync(string tenantId, string memberId, DateTime asOfDate, CancellationToken ct = default)
    {
        var s = _snapshots.FirstOrDefault(x =>
            x.TenantId == tenantId &&
            x.MemberId == memberId &&
            x.PlanYearStart <= asOfDate &&
            x.PlanYearEnd >= asOfDate);
        return Task.FromResult(s is null ? null : Copy(s));
    }

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetSnapshotsAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        IReadOnlyList<AccumulatorSnapshot> result = _snapshots
            .Where(s => s.TenantId == tenantId && s.MemberId == memberId)
            .OrderByDescending(s => s.PlanYearStart)
            .Select(Copy)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> TryReplaceSnapshotAsync(AccumulatorSnapshot snapshot, long expectedVersion, CancellationToken ct = default)
    {
        if (ThrowOnNextSnapshotWrite)
        {
            ThrowOnNextSnapshotWrite = false;
            throw new IOException("simulated crash before the snapshot write");
        }
        var current = _snapshots.FirstOrDefault(s => s.TenantId == snapshot.TenantId && s.Id == snapshot.Id);
        if ((current?.Version ?? 0) != expectedVersion) return Task.FromResult(false);
        _snapshots.RemoveAll(s => s.TenantId == snapshot.TenantId && s.Id == snapshot.Id);
        _snapshots.Add(Copy(snapshot));
        return Task.FromResult(true);
    }

    public async Task<bool> TryAppendEventAsync(AccumulatorEvent evt, CancellationToken ct = default)
    {
        if (BeforeNextAppend is { } hook)
        {
            BeforeNextAppend = null;
            await hook(evt);
        }
        if (Events.Any(e => e.Id == evt.Id)) return false;
        if (Events.Any(e => e.TenantId == evt.TenantId && e.EventId == evt.EventId)) return false;
        if (Events.Any(e => e.TenantId == evt.TenantId && e.AggregateId == evt.AggregateId && e.Version == evt.Version))
            return false;
        if (evt.EventType == "ClaimReversed"
            && Events.Any(e => e.TenantId == evt.TenantId && e.EventType == "ClaimReversed" && e.SourceClaimId == evt.SourceClaimId))
            return false;
        Events.Add(Copy(evt));
        return true;
    }

    /// <summary>Appends a row as-is (test setup, e.g. a legacy row).</summary>
    public void AddEvent(AccumulatorEvent evt) => Events.Add(Copy(evt));

    public Task<IReadOnlyList<AccumulatorEvent>> GetAggregateEventsAsync(
        string tenantId, string aggregateId, long afterVersion = 0, CancellationToken ct = default)
    {
        IReadOnlyList<AccumulatorEvent> result = Events
            .Where(e => e.TenantId == tenantId && e.AggregateId == aggregateId && e.Version > afterVersion)
            .OrderBy(e => e.Version)
            .Select(Copy)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<AccumulatorEvent?> GetClaimReversedEventAsync(string tenantId, string claimId, CancellationToken ct = default) =>
        Task.FromResult(Events.FirstOrDefault(e =>
            e.TenantId == tenantId && e.EventType == "ClaimReversed" && e.SourceClaimId == claimId));

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

    public Task<AccumulatorEvent?> GetClaimAppliedEventAsync(string tenantId, string claimId, CancellationToken ct = default) =>
        Task.FromResult(Events.LastOrDefault(e =>
            e.TenantId == tenantId && e.EventType == "ClaimApplied" && e.SourceClaimId == claimId));
}

public class InMemoryProcessedClaimStore : IProcessedClaimStore
{
    private readonly Dictionary<string, ProcessedClaim> _map = new();

    /// <summary>The store's clock; tests move it past the lease.</summary>
    public DateTime Now { get; set; } = DateTime.UtcNow;

    public TimeSpan Lease { get; set; } = ProcessedClaimLease.Timeout;

    public Task<BeginClaimOutcome> TryBeginAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{claimId}";
        if (_map.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing.Outcome, "Pending", StringComparison.Ordinal))
                return Task.FromResult(BeginClaimOutcome.AlreadyApplied);
            // Pending within the lease = in flight; older = crashed, take it over.
            if (existing.ProcessedAt > Now - Lease)
                return Task.FromResult(BeginClaimOutcome.InProgress);
            existing.ProcessedAt = Now;
            return Task.FromResult(BeginClaimOutcome.Proceed);
        }
        _map[key] = new ProcessedClaim
        {
            Id = key,
            TenantId = tenantId,
            ClaimId = claimId,
            ProcessedAt = Now,
            Outcome = "Pending"
        };
        return Task.FromResult(BeginClaimOutcome.Proceed);
    }

    public Task ReleaseAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{claimId}";
        if (_map.TryGetValue(key, out var p) && string.Equals(p.Outcome, "Pending", StringComparison.Ordinal))
            _map.Remove(key);
        return Task.CompletedTask;
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

    /// <summary>
    /// Runs once, before the next read of <see cref="HookClaimId"/>'s marker
    /// (to interleave another worker between two reads of the service).
    /// </summary>
    public Func<Task>? BeforeNextGet { get; set; }

    public string? HookClaimId { get; set; }

    public async Task<ProcessedClaim?> GetAsync(string tenantId, string claimId, CancellationToken ct = default)
    {
        if (BeforeNextGet is { } hook && claimId == HookClaimId)
        {
            BeforeNextGet = null;
            await hook();
        }
        var key = $"{tenantId}:{claimId}";
        return _map.TryGetValue(key, out var p) ? p : null;
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
