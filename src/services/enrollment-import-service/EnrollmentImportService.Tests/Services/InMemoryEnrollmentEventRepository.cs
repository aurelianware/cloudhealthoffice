using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// Test double for <see cref="IEnrollmentEventRepository"/>. Models the same uniqueness
/// rules as Cosmos (unique on EventId per partition; unique on Version per partition) so
/// publisher tests exercise realistic conflict paths.
/// </summary>
internal sealed class InMemoryEnrollmentEventRepository : IEnrollmentEventRepository
{
    private readonly object _lock = new();
    private readonly List<EnrollmentEvent> _events = new();
    public int VersionConflictsToInject { get; set; }

    public IReadOnlyList<EnrollmentEvent> AllEvents
    {
        get { lock (_lock) return _events.ToArray(); }
    }

    public Task<EnrollmentEventAppendResult> AppendAsync(EnrollmentEvent evt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (VersionConflictsToInject > 0)
            {
                VersionConflictsToInject--;
                return Task.FromResult(new EnrollmentEventAppendResult(evt, Appended: false));
            }

            var existingById = _events.FirstOrDefault(e =>
                e.TenantId == evt.TenantId && e.MemberId == evt.MemberId && e.EventId == evt.EventId);
            if (existingById != null)
                return Task.FromResult(new EnrollmentEventAppendResult(existingById, Appended: false));

            var versionTaken = _events.Any(e =>
                e.TenantId == evt.TenantId && e.MemberId == evt.MemberId && e.Version == evt.Version);
            if (versionTaken)
                return Task.FromResult(new EnrollmentEventAppendResult(evt, Appended: false));

            _events.Add(Clone(evt));
            return Task.FromResult(new EnrollmentEventAppendResult(Clone(evt), Appended: true));
        }
    }

    public Task<EnrollmentEventPage> ListByMemberAsync(
        string tenantId, string memberId, EnrollmentEventQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IEnumerable<EnrollmentEvent> q = _events
                .Where(e => e.TenantId == tenantId && e.MemberId == memberId);
            if (query.EventType.HasValue)
                q = q.Where(e => e.EventType == query.EventType.Value);
            if (query.FromUtc.HasValue)
                q = q.Where(e => e.OccurredAt >= query.FromUtc.Value);
            if (query.ToUtc.HasValue)
                q = q.Where(e => e.OccurredAt <= query.ToUtc.Value);

            var items = q.OrderByDescending(e => e.Version)
                .Take(query.Limit)
                .Select(Clone)
                .ToList();
            return Task.FromResult(new EnrollmentEventPage(items, ContinuationToken: null));
        }
    }

    public Task<EnrollmentEvent?> GetByIdAsync(
        string tenantId, string memberId, string eventId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var hit = _events.FirstOrDefault(e =>
                e.TenantId == tenantId && e.MemberId == memberId && e.EventId == eventId);
            return Task.FromResult(hit == null ? null : Clone(hit));
        }
    }

    public Task<int> GetNextVersionAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var max = _events
                .Where(e => e.TenantId == tenantId && e.MemberId == memberId)
                .Select(e => (int?)e.Version)
                .Max() ?? 0;
            return Task.FromResult(max + 1);
        }
    }

    private static EnrollmentEvent Clone(EnrollmentEvent src) => new()
    {
        Id = src.Id,
        PartitionKey = src.PartitionKey,
        TenantId = src.TenantId,
        MemberId = src.MemberId,
        EventId = src.EventId,
        EventType = src.EventType,
        Version = src.Version,
        SchemaVersion = src.SchemaVersion,
        OccurredAt = src.OccurredAt,
        EventDate = src.EventDate,
        RetroEffectiveDate = src.RetroEffectiveDate,
        SourceBatchId = src.SourceBatchId,
        TransactionId = src.TransactionId,
        MaintenanceType = src.MaintenanceType,
        MaintenanceReason = src.MaintenanceReason,
        Source = src.Source,
        ActorId = src.ActorId,
        CorrelationId = src.CorrelationId,
        Payload = src.Payload?.DeepClone() as System.Text.Json.Nodes.JsonObject,
        RawSegment = src.RawSegment
    };
}
